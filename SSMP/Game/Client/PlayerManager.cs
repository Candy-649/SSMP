using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Animation.Effects;
using SSMP.Animation.Effects.Tools;
using SSMP.Api.Client;
using SSMP.Fsm;
using SSMP.Game.Client.Skin;
using SSMP.Game.Settings;
using SSMP.Hooks;
using SSMP.Internals;
using SSMP.Networking.Client;
using SSMP.Ui;
using SSMP.Util;
using TMProOld;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Math_Vector2 = SSMP.Math.Vector2;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Class that manages player objects, spawning and recycling thereof.
/// </summary>
internal class PlayerManager : IPlayerManager {
    /// <summary>
    /// The name of the game object for the player container prefab.
    /// </summary>
    private const string PlayerContainerPrefabName = "Player Container Prefab";

    /// <summary>
    /// The name of the game object for the player object prefab.
    /// </summary>
    private const string PlayerObjectPrefabName = "Player Prefab";

    /// <summary>
    /// The depth that the character of another player is drawn at. The camera orders sprites by depth alone and draws
    /// the nearer one on top, and the game holds its own hero at 0.004 every frame, so at the depth of 0 that the
    /// positions of other players come in at, their character was drawn over the player's own. Just behind the hero,
    /// it keeps the place among everything else in the room that the other player's own hero has in their game.
    /// </summary>
    private const float PlayerObjectDepth = 0.0041f;

    /// <summary>
    /// The name (and prefix) of the game object for player containers.
    /// </summary>
    internal const string PlayerContainerName = "Player Container";

    /// <summary>
    /// The name of the game object for the username of players.
    /// </summary>
    private const string UsernameObjectName = "Username";

    /// <summary>
    /// The initial size of the pool of player container objects to be pre-instantiated.
    /// </summary>
    private const ushort InitialPoolSize = 64;

    /// <summary>
    /// The current server settings.
    /// </summary>
    private readonly ServerSettings _serverSettings;

    /// <summary>
    /// The settings of this machine, which say how this player's own name is drawn.
    /// </summary>
    private readonly Settings.ModSettings _modSettings;

    /// <summary>
    /// The colours a player may draw their own name in.
    ///
    /// Red, white and green are left out of the defaults on purpose: the game already says things with them - white
    /// is the flash of something being hit, red is damage, green is poison - and a name that borrows one of those
    /// reads as the game saying something. They are still here for anyone who wants them, except white, which is
    /// what the name is when none of this is on.
    /// </summary>
    internal static readonly Dictionary<string, Color> OwnNameColours = new(StringComparer.OrdinalIgnoreCase) {
        ["yellow"] = new Color(1f, 0.9f, 0.2f),
        ["green"] = new Color(0.3f, 1f, 0.4f),
        ["cyan"] = new Color(0.3f, 0.9f, 1f),
        ["orange"] = new Color(1f, 0.6f, 0.15f),
        ["red"] = new Color(1f, 0.35f, 0.35f)
    };

    /// <summary>
    /// The colours a player may draw their own name in, in the order they are offered. A dictionary does not
    /// promise an order and this is shown as a row of them, which has to look the same every time it is opened.
    /// </summary>
    internal static readonly string[] OwnNameColourNames = ["yellow", "green", "cyan", "orange", "red"];

    /// <summary>
    /// The colour this player picked for their own name, which also marks their head on the map and their hero in a
    /// fight: yellow when the setting names none of the colours.
    /// </summary>
    /// <param name="modSettings">The settings of this machine.</param>
    /// <returns>The colour.</returns>
    internal static Color OwnNameColour(ModSettings modSettings) =>
        OwnNameColours.TryGetValue(modSettings.OwnNameColour ?? "", out var colour) ? colour : OwnNameColours["yellow"];

    /// <summary>
    /// The colour of this player's own outlines, of their head on the map and of their hero in fights: the colour that
    /// they chose for their cloak in Player Color, where they chose to follow it and didn't keep the cloak's own - also
    /// while a skin keeps the cloak from showing it - or else the colour of their name.
    /// </summary>
    /// <param name="modSettings">The settings of this machine.</param>
    /// <returns>The colour.</returns>
    internal static Color OwnOutlineColour(ModSettings modSettings) =>
        modSettings.OutlineInCloakColour && Skin.PlayerColorMod.ChosenColour() is { } cloak
            ? new Color(cloak.r, cloak.g, cloak.b, 1f)
            : OwnNameColour(modSettings);

    /// <summary>
    /// How wide the black edge around a name is normally, and how wide it is when the name is drawn heavier.
    ///
    /// The edge does more than the weight of the letters does: the font is a thin one, and against a busy dark
    /// background thin letters disappear whatever weight they are drawn at, while an edge holds them apart from
    /// whatever is behind them.
    /// </summary>
    private const float NameOutline = 0.2f, BoldNameOutline = 0.4f;

    /// <summary>
    /// The net client for accessing average RTT.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The skin manager instance.
    /// </summary>
    private readonly SkinManager _skinManager;

    /// <summary>
    /// What was copied of the local hero for each character of another player, by the object that draws it.
    /// </summary>
    private readonly Dictionary<GameObject, FigureCopies> _figureCopies = new();

    /// <summary>
    /// The animators found on a copy that is being dressed (see <see cref="DressHeroCopy"/>), kept from one copy to
    /// the next.
    /// </summary>
    private static readonly List<tk2dSpriteAnimator> DressedAnimators = [];

    /// <summary>
    /// The sprites found on a copy that is being dressed (see <see cref="DressHeroCopy"/>), kept from one copy to the
    /// next.
    /// </summary>
    private static readonly List<tk2dBaseSprite> DressedSprites = [];

    /// <summary>
    /// Reference to the client player data dictionary (<see cref="ClientManager._playerData"/>)
    /// from <see cref="ClientManager"/>.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The player container prefab GameObject.
    /// </summary>
    private GameObject? _playerContainerPrefab;

    /// <summary>
    /// A queue of pre-instantiated players that will be used when spawning a player.
    /// </summary>
    private readonly Queue<GameObject> _inactivePlayers;

    /// <summary>
    /// The collection of active players spawned from and not in the player pool.
    /// </summary>
    private readonly Dictionary<ushort, GameObject> _activePlayers;

    /// <inheritdoc/>
    public Team LocalPlayerTeam { get; private set; } = Team.None;

    /// <summary>
    /// Event for when the local player's team changes after being received from the server.
    /// </summary>
    public event Action<Team>? LocalPlayerTeamChangeEvent;

    /// <summary>
    /// Event for when any player's team changes after being received from the server.
    /// </summary>
    public event Action<IClientPlayer, Team>? PlayerTeamChangeEvent;

    public PlayerManager(
        ServerSettings serverSettings,
        Settings.ModSettings modSettings,
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData
    ) {
        _serverSettings = serverSettings;
        _modSettings = modSettings;
        _netClient = netClient;

        _skinManager = new SkinManager(() => _serverSettings.AllowSkins);
        SlashSkin.Start(modSettings);

        _playerData = playerData;

        _inactivePlayers = new Queue<GameObject>();
        _activePlayers = new Dictionary<ushort, GameObject>();
    }

    /// <summary>
    /// Initialize the player manager.
    /// </summary>
    public void Initialize() {
    }

    /// <summary>
    /// Updates interpolation for all active players.
    /// Also feeds the current network RTT (round-trip time) into each player's
    /// interpolation so it can adapt smoothing/prediction to the actual ping.
    /// </summary>
    /// <param name="dt">The delta time for this frame.</param>
    /// <param name="currentRttMs">The current average round-trip time in milliseconds.</param>
    public void UpdateInterpolations(float dt, float currentRttMs) {
        foreach (var container in _activePlayers.Values) {
            // Cache component reference if accessed frequently
            if (container.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
                // Tell the interpolator what the current ping is, so it can pick
                // a smoothing tier (LAN / Excellent / Good / Fair / Poor).
                interpolation.AdaptToRTT(currentRttMs);
                interpolation.ManualUpdate(dt);
            }
        }
    }

    /// <summary>
    /// Register the relevant hooks for player-related operations.
    /// </summary>
    public void RegisterHooks() {
        _skinManager.RegisterHooks();

        CustomHooks.HeroControllerStartAction += HeroControllerOnStart;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;

        var updateTint = typeof(CharacterTint).GetMethod(
            "UpdateTint",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(bool)],
            null
        );
        if (updateTint == null) {
            Logger.Error("Could not find how the game tints characters, so other players keep their own colours");
        } else {
            _updateTintHook = new Hook(updateTint, (Action<UpdateTintMethod, CharacterTint, bool>) OnUpdateTint);
        }
    }

    /// <summary>
    /// Deregister the relevant hooks for player-related operations.
    /// </summary>
    public void DeregisterHooks() {
        _skinManager.DeregisterHooks();

        CustomHooks.HeroControllerStartAction -= HeroControllerOnStart;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;

        _updateTintHook?.Dispose();
        _updateTintHook = null;
    }

    /// <summary>
    /// The hook that notices the game changing the tint of the player's own character.
    /// </summary>
    private Hook? _updateTintHook;

    /// <summary>
    /// The method that the game changes the tint of a character with, at once or fading to it.
    /// </summary>
    private delegate void UpdateTintMethod(CharacterTint self, bool instant);

    /// <summary>
    /// Gives the characters of other players the tint that the room has just given the player's own character.
    ///
    /// Parts of some rooms tint every character in them, and find what walks in by its collider. The collider of the
    /// character of another player is on a layer they cannot see, kept there so that traps and plates do not go off for
    /// it, so it stood in its own colours, lighter than the player's own character right beside it. It now takes the
    /// tint whenever the game changes the player's own, with the game's own fade, as the two mostly stand in the same
    /// part of a room. The ones waiting in the pool take it at once, so that one taken out for a player who walks in
    /// comes with the tint of the room as it is now.
    /// </summary>
    private void OnUpdateTint(UpdateTintMethod orig, CharacterTint self, bool instant) {
        orig(self, instant);

        var hero = HeroController.SilentInstance;
        if (hero == null || self.gameObject != hero.gameObject) {
            return;
        }

        foreach (var container in _activePlayers.Values) {
            TintLikeTheHero(container);
        }

        foreach (var container in _inactivePlayers) {
            TintLikeTheHero(container);
        }

        void TintLikeTheHero(GameObject container) {
            var tint = container == null ? null : container.GetComponentInChildren<CharacterTint>(true);
            if (tint != null) {
                // A fade runs on the object of the character, which cannot run anything while it is switched off
                orig(tint, instant || !tint.isActiveAndEnabled);
            }
        }
    }

    /// <summary>
    /// Callback method for when the HeroController starts so we can create the player pool.
    /// </summary>
    private void HeroControllerOnStart() {
        TryCreatePlayerPool();
        OwnSkin.LetGo();
    }

    /// <summary>
    /// Called every Unity update for updating interpolations of players.
    /// </summary>
    private void OnUpdate() {
        foreach (var container in _activePlayers.Values) {
            // Cache component reference if accessed frequently
            if (container.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
                // Tell the interpolator what the current ping is, so it can pick
                // a smoothing tier (LAN / Excellent / Good / Fair / Poor).
                interpolation.AdaptToRTT(_netClient.UpdateManager.AverageRtt);
                interpolation.ManualUpdate(Time.deltaTime);
            }
        }
    }

    /// <summary>
    /// Try to create the initial pool of player objects if it hasn't been created yet.
    /// </summary>
    private void TryCreatePlayerPool() {
        if (_playerContainerPrefab) {
            return;
        }

        // Create a player container prefab, used to spawn players
        _playerContainerPrefab = new GameObject(PlayerContainerPrefabName);

        _playerContainerPrefab.AddComponent<PredictiveInterpolation>();

        var playerPrefab = new GameObject(
            PlayerObjectPrefabName,
            typeof(BoxCollider2D),
            typeof(MeshFilter),
            typeof(MeshRenderer),
            typeof(tk2dSprite),
            typeof(tk2dSpriteAnimator),
            typeof(Rigidbody2D),
            typeof(CoroutineCancelComponent),
            typeof(CharacterTint)
        );

        playerPrefab.transform.SetParent(_playerContainerPrefab.transform);
        playerPrefab.transform.localPosition = new Vector3(0f, 0f, PlayerObjectDepth);

        // Now we need to copy over a lot of variables from the local player object
        var localPlayerObject = HeroController.instance.gameObject;

        // Obtain colliders from both objects
        var collider = playerPrefab.GetComponent<BoxCollider2D>();
        // We're not using the fact that Hornet has a BoxCollider as opposed to any other collider
        var localCollider = localPlayerObject.GetComponent<Collider2D>();

        collider.isTrigger = true;
        collider.offset = localCollider.offset;
        collider.size = localCollider is BoxCollider2D localBoxCollider
            ? localBoxCollider.size
            : localCollider.bounds.size;
        collider.enabled = true;

        // Set Rigidbody properties
        var rigidbody = playerPrefab.GetComponent<Rigidbody2D>();
        rigidbody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rigidbody.gravityScale = 0f;
        rigidbody.bodyType = RigidbodyType2D.Kinematic;
        rigidbody.simulated = true;

        // Set up sprite flash. Must be deactivated beforehand to fill properties in awake
        playerPrefab.SetActive(false);
        var flash = playerPrefab.AddComponent<SpriteFlash>();
        flash.parents = [];
        flash.children = [];

        // Add some extra gameObjects related to animation effects
        new GameObject("Attacks").transform.SetParent(playerPrefab.transform);
        new GameObject("Bind Effects").transform.SetParent(playerPrefab.transform);
        new GameObject("Effects").transform.SetParent(playerPrefab.transform);
        new GameObject("Charm Effects").transform.SetParent(playerPrefab.transform);
        new GameObject("Special Attacks").transform.SetParent(playerPrefab.transform);
        new GameObject("Tool Effects").transform.SetParent(playerPrefab.transform);

        // The name goes at the depth of the character it hangs over, for the same reason: the name over the player's
        // own head is held at the depth of the hero, and the name of another player at 0 was drawn over it
        var usernameObject = CreateUsername(_playerContainerPrefab);
        usernameObject.transform.localPosition += new Vector3(0f, 0f, PlayerObjectDepth);

        _playerContainerPrefab.SetActive(false);
        Object.DontDestroyOnLoad(_playerContainerPrefab);

        // Instantiate all the player objects for the pool
        for (ushort i = 0; i < InitialPoolSize; i++) {
            _inactivePlayers.Enqueue(CreateNewPlayerContainer());
        }
    }

    /// <summary>
    /// Create a new player container object from the <see cref="_playerContainerPrefab"/> prefab.
    /// </summary>
    /// <returns>A new GameObject representing the player container.</returns>
    private GameObject CreateNewPlayerContainer() {
        var playerContainer = Object.Instantiate(_playerContainerPrefab);
        if (playerContainer == null) {
            throw new Exception("Could not create new player container, instantiation failed");
        }

        Object.DontDestroyOnLoad(playerContainer);
        playerContainer.name = PlayerContainerName;

        var playerObj = playerContainer.FindGameObjectInChildren(PlayerObjectPrefabName);
        if (playerObj == null) {
            throw new Exception("Player object could not be found in instantiated player container");
        }

        MakeUniqueSpriteAnimator(playerObj);

        return playerContainer;
    }

    /// <summary>
    /// Update the position of a player with the given position.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="position">The new position of the player.</param>
    /// <param name="sequence">The sequence number of the packet it arrived in.</param>
    public void UpdatePosition(ushort id, Math_Vector2 position, ushort sequence) {
        if (!_playerData.TryGetValue(id, out var playerData) || !playerData.IsInLocalScene) {
            return;
        }

        // An older position arriving after a newer one is thrown away, or the player is put back over ground they
        // have already covered
        if (!playerData.PositionSequence.Accepts(sequence, out var packetsCovered)) {
            return;
        }

        var playerContainer = playerData.PlayerContainer;
        if (playerContainer) {
            var unityPosition = new Vector3(position.X, position.Y);

            playerContainer.GetComponent<PredictiveInterpolation>().SetNewPosition(unityPosition, packetsCovered);
        }
    }

    /// <summary>
    /// Update the scale of a player with the given boolean.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="scale">The new scale as a boolean, true indicating an X scale of 1,
    /// false indicating an X scale of -1.</param>
    public void UpdateScale(ushort id, bool scale) {
        if (!_playerData.TryGetValue(id, out var playerData) || !playerData.IsInLocalScene) {
            return;
        }

        var playerObject = playerData.PlayerObject;
        if (playerObject == null) {
            Logger.Warn("Could not update scale of player, because player object is null");
            return;
        }

        SetPlayerObjectBoolScale(playerObject, scale);
    }

    /// <summary>
    /// Sets the scale of a player object from a boolean.
    /// </summary>
    /// <param name="playerObject">The GameObject representing the player.</param>
    /// <param name="scale">The new scale as a boolean, true indicating a X scale of 1,
    /// false indicating an X scale of -1.</param>
    private void SetPlayerObjectBoolScale(GameObject playerObject, bool scale) {
        if (!playerObject) {
            return;
        }

        var transform = playerObject.transform;
        var localScale = transform.localScale;
        var currentScaleX = localScale.x;

        if (currentScaleX > 0 != scale) {
            transform.localScale = new Vector3(
                currentScaleX * -1,
                localScale.y,
                localScale.z
            );
        }
    }

    /// <summary>
    /// Get the player object given the player ID.
    /// </summary>
    /// <param name="id">The player ID.</param>
    /// <returns>The GameObject for the player.</returns>
    public GameObject? GetPlayerObject(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData) || !playerData.IsInLocalScene) {
            Logger.Debug($"Tried to get the player data that does not exists for ID {id}");
            return null;
        }

        return playerData.PlayerObject;
    }

    /// <summary>
    /// Callback method for when the local user disconnects. Will reset all player related things
    /// to their default values.
    /// </summary>
    public void OnDisconnect() {
        // Reset the local player's team
        LocalPlayerTeam = Team.None;

        PlayerTargetRegistry.ClearRemotePlayers();
        NeedolinCoop.ClearRemotePerformers();

        // Clear all players
        RecycleAllPlayers();

        // Remove name
        RemoveNameFromLocalPlayer();

        // Nobody else wears anything any more: the art of their skins is let go, and what was said about them may be
        // said again on the next connection
        _skinManager.Want([]);
        _reportedSkinProblems.Clear();
    }

    /// <summary>
    /// Recycle the player container of the player with the given ID back into the queue.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    public void RecyclePlayer(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Tried to recycle player that does not exists for ID {id}");
            return;
        }

        RecyclePlayerByData(playerData);
    }

    /// <summary>
    /// Recycle the player container of the player with the given player data.
    /// </summary>
    /// <param name="playerData">The player data of the player.</param>
    private void RecyclePlayerByData(ClientPlayerData playerData) {
        PlayerTargetRegistry.UnregisterRemotePlayer(playerData.PlayerObject);
        NeedolinCoop.RemoveRemotePerformer(playerData.PlayerObject);

        // First reset the player
        ResetPlayer(playerData);

        // Remove the player container from the player data
        playerData.PlayerContainer = null;

        // Find the player container in the active containers if it exists
        if (!_activePlayers.TryGetValue(playerData.Id, out var container)) {
            return;
        }

        container.SetActive(false);
        container.name = PlayerContainerName;

        _activePlayers.Remove(playerData.Id);
        _inactivePlayers.Enqueue(container);
    }

    /// <summary>
    /// Recycle all existing players. <seealso cref="RecyclePlayer"/>
    /// </summary>
    public void RecycleAllPlayers() {
        foreach (var id in _playerData.Keys) {
            // Recycle player
            RecyclePlayer(id);
        }
    }

    /// <summary>
    /// Reset the player with the given player data.
    /// </summary>
    /// <param name="playerData">The player data of the player.</param>
    private void ResetPlayer(ClientPlayerData playerData) {
        var container = playerData.PlayerContainer;
        if (!container) {
            return;
        }

        ResetPlayerContainer(container);
    }

    /// <summary>
    /// Reset the given player container by removing all game objects not inherent to it.
    /// </summary>
    /// <param name="playerContainer">The game object representing the player container.</param>
    private void ResetPlayerContainer(GameObject playerContainer) {
        // Destroy all descendants and components that weren't originally on the container object
        foreach (Transform child in playerContainer.transform) {
            if (child.name != PlayerObjectPrefabName) {
                continue;
            }

            // The copied parts of the hero and the look of the player's needle stay, and must not show on whoever
            // the character is used for next, and the things that the player's tools left in the room are gone with
            // the room
            HeroHidden.Reset(child.gameObject);
            HeroRoomFigure.Reset(child.gameObject);
            _skinManager.Undress(child.gameObject);
            if (_figureCopies.TryGetValue(child.gameObject, out var copies)) {
                LetGoOfGoneRoomCopies(child.gameObject, copies);
            }
            HeroChildEffects.ResetCopies(child.gameObject);
            HeroChildParticles.ResetCopies(child.gameObject);
            HeroDust.Reset(child.gameObject);
            HeroHarpoon.Reset(child.gameObject);
            HeroHit.Reset(child.gameObject);
            ImbuedNail.Stop(child.gameObject);
            ThrownTool.RemoveCopies(child.gameObject);

            foreach (Transform grandChild in child) {
                if (grandChild.name is "Attacks" or "Effects" or "Spells") {
                    // Remove all grandchildren from the player prefab's children; there should be none
                    foreach (Transform greatGrandChild in grandChild) {
                        Logger.Debug(
                            $"Destroying child of {grandChild.name}: {greatGrandChild.name}, type: {greatGrandChild.GetType()}"
                        );
                        Object.Destroy(greatGrandChild.gameObject);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Spawn a new player object with the given data.
    /// </summary>
    /// <param name="playerData">The client player data for the player.</param>
    /// <param name="position">The Vector2 denoting the position of the player.</param>
    /// <param name="scale">The boolean representing the scale of the player.</param>
    public void SpawnPlayer(
        ClientPlayerData playerData,
        Math_Vector2 position,
        bool scale
    ) {
        // First recycle the player by player data if they have an active container
        RecyclePlayerByData(playerData);

        var playerContainer =
            // Create a new player container
            _inactivePlayers.Count <= 0
                ? CreateNewPlayerContainer()
                :
                // Dequeue a player container from the inactive players
                _inactivePlayers.Dequeue();

        playerContainer.name = $"{PlayerContainerName} {playerData.Id}";

        _activePlayers[playerData.Id] = playerContainer;

        playerContainer.transform.SetPosition2D(position.X, position.Y);

        var playerObject = playerContainer.FindGameObjectInChildren(PlayerObjectPrefabName);
        if (playerObject == null) {
            throw new Exception("Player object could not be found in player container while spawning player");
        }

        SetPlayerObjectBoolScale(playerObject, scale);

        // Set container and children active
        playerContainer.SetActive(true);
        playerContainer.SetActiveChildren(true);

        AddNameToPlayer(playerContainer, playerData.Username, playerData.Team);

        // Dressed in what the player wears through skin mods
        _skinManager.Dress(playerObject, playerData.Skin);

        // Store the player data
        playerData.PlayerContainer = playerContainer;
        playerData.PlayerObject = playerObject;

        // Ignore collisions between local player and remote player
        if (HeroController.instance != null) {
            var localColliders = HeroController.instance.GetComponentsInChildren<Collider2D>(true);
            var remoteColliders = playerContainer.GetComponentsInChildren<Collider2D>(true);
            foreach (var localCol in localColliders) {
                foreach (var remoteCol in remoteColliders) {
                    if (localCol != null && remoteCol != null) {
                        Physics2D.IgnoreCollision(localCol, remoteCol, true);
                    }
                }
            }
        }

        PlayerTargetRegistry.RegisterRemotePlayer(playerObject);
    }

    /// <summary>
    /// Create a unique copy of a player object's sprite animator so that skins are unique to each player.
    /// </summary>
    /// <param name="playerObject">The player object with the sprite animator component.</param>
    private void MakeUniqueSpriteAnimator(GameObject playerObject) {
        var localPlayer = HeroController.instance;
        // Copy over mesh filter variables
        var meshFilter = playerObject.GetComponent<MeshFilter>();
        var mesh = meshFilter.mesh;
        var localMesh = localPlayer.GetComponent<MeshFilter>().sharedMesh;

        mesh.vertices = localMesh.vertices;
        mesh.normals = localMesh.normals;
        mesh.uv = localMesh.uv;
        mesh.triangles = localMesh.triangles;
        mesh.tangents = localMesh.tangents;

        // Copy mesh renderer material
        var meshRenderer = playerObject.GetComponent<MeshRenderer>();
        meshRenderer.material = new Material(localPlayer.GetComponent<MeshRenderer>().material);

        // Copy over animation library
        var spriteAnimator = playerObject.GetComponent<tk2dSpriteAnimator>();
        // Make a smart copy of the sprite animator library so we can
        // modify the animator without having to worry about other player objects
        var copies = new FigureCopies();
        _figureCopies[playerObject] = copies;
        var library = localPlayer.GetComponent<tk2dSpriteAnimator>().Library;
        var copiedSheets = new Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData>();
        spriteAnimator.Library = CopyUtil.SmartCopySpriteAnimation(library, playerObject, copiedSheets, copies.Objects);
        copies.Libraries[library] = spriteAnimator.Library;
        copies.Own.Add(spriteAnimator.Library);
        copies.CopiedWith[library] = copies.Objects;
        copies.TakeSheets(copiedSheets, ofTheHero: true);

        // Its own sheets of sprites, so that it can wear a skin of its own
        _skinManager.RegisterFigure(playerObject, copiedSheets);
    }

    /// <summary>
    /// The clip that the character of another player plays for a clip of one of the libraries that the local hero's
    /// crests play instead of its own: the character's own copy, from its own copy of that library, made the first
    /// time it plays from it. Those libraries draw from sheets of sprites of their own, so a clip played from the
    /// original drew the character from the local hero's sheets, in the local player's skin and cloak colour.
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="library">The library of the crest.</param>
    /// <param name="clip">The clip of that library.</param>
    /// <returns>The character's copy of the clip, or the clip itself for an object that is no such character.</returns>
    public tk2dSpriteAnimationClip FigureClip(
        GameObject playerObject,
        tk2dSpriteAnimation library,
        tk2dSpriteAnimationClip clip
    ) {
        if (!_figureCopies.TryGetValue(playerObject, out var copies)) {
            return clip;
        }

        // A library that a figure of a room played from first was copied as the room's
        FigureLibrary(playerObject, copies, library, SheetRole.Body);
        return copies.CopiedWith.TryGetValue(library, out var objects) && objects.TryGetValue(clip, out var copy)
            ? (tk2dSpriteAnimationClip) copy
            : clip;
    }

    /// <summary>
    /// Dresses a figure that the room shows in place of another player as that player's character (see
    /// <see cref="HeroRoomFigure"/>). It is a copy of this game's own figure, which played from this game's library and
    /// drew from this game's sheets of sprites, so the other player showed in the local player's skin and cloak colour
    /// whenever the room showed them by a figure of its own. It now plays from the character's own copy of that library
    /// and draws from the character's own copies of those sheets, which wear what that player wears.
    /// It is dressed before it is switched on, as Player Color chooses once for each object, by the sheet that it first
    /// draws from, whether to colour it in the local player's colour.
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="standIn">The copy of the figure, with every part of it that plays or draws.</param>
    public void DressStandIn(GameObject playerObject, GameObject standIn) {
        if (!_figureCopies.TryGetValue(playerObject, out var copies)) {
            return;
        }

        foreach (var animator in standIn.GetComponentsInChildren<tk2dSpriteAnimator>(true)) {
            if (animator.Library != null &&
                FigureLibrary(playerObject, copies, animator.Library, SheetRole.Room) is { } library) {
                animator.Library = library;
            }
        }

        // The picture it shows until its first clip starts. A sprite that is switched off only takes the sheet, which
        // it draws from as it wakes
        foreach (var sprite in standIn.GetComponentsInChildren<tk2dBaseSprite>(true)) {
            if (sprite.Collection == null || !copies.Sheets.TryGetValue(sprite.Collection, out var sheet) ||
                sheet == null) {
                continue;
            }

            if (sprite.gameObject.activeInHierarchy) {
                sprite.SetSprite(sheet, sprite.spriteId);
            } else {
                sprite.Collection = sheet;
            }
        }
    }

    /// <summary>
    /// The character's own copy of a sheet of sprites, made the first time it is needed and dressed in what the player
    /// wears; or null for an object that is no such character, or a sheet that could not be copied. A creature that
    /// holds a player draws them in its own sprites, which are drawn from it while it does
    /// (see <see cref="HeldLook"/>).
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="sheet">The original sheet.</param>
    public tk2dSpriteCollectionData? FigureSheet(GameObject playerObject, tk2dSpriteCollectionData sheet) {
        if (!_figureCopies.TryGetValue(playerObject, out var copies)) {
            return null;
        }

        if (copies.Sheets.TryGetValue(sheet, out var copy) && copy != null) {
            return copy;
        }

        if (copies.Failed.Contains(sheet)) {
            return null;
        }

        try {
            var roomCopy = new RoomCopy(sheet, null);
            copy = CopyUtil.SmartCopySpriteCollection(sheet, playerObject, roomCopy.Objects);
            var copiedSheets = new Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData>();
            roomCopy.Took(null, copiedSheets);
            copies.RoomCopies.Add(roomCopy);
            copies.TakeSheets(copiedSheets, ofTheHero: false);
            _skinManager.AddSheets(playerObject, copiedSheets, SheetRole.Room);
        } catch (Exception e) {
            Logger.Warn($"Could not copy the sheet of sprites '{sheet.name}' for the character of another player: " +
                        e.Message);
            copies.Failed.Add(sheet);
            return null;
        }

        LetGoOfGoneRoomCopies(playerObject, copies);
        return copy;
    }

    /// <summary>
    /// The character of another player that a part of the world belongs to: the first character from it upwards, or
    /// null for a part of none.
    /// </summary>
    /// <param name="part">The part.</param>
    public GameObject? CharacterOf(Transform part) {
        for (var current = part; current != null; current = current.parent) {
            if (_figureCopies.ContainsKey(current.gameObject)) {
                return current.gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// Dresses a copy of one of the local hero's own objects, or of the game's own, that shows what another player
    /// does, as that player's character (see <see cref="HeroCopies"/>). It played from the same libraries of clips and
    /// drew from the same sheets of sprites as its original, so the other player's slashes, strikes, silk skills and
    /// the rest showed in the local player's skin and cloak colour. Its animators now play from the character's own
    /// copies of those libraries, and its sprites draw from the character's own copies of those sheets, which wear what
    /// that player wears. Those libraries and sheets stay loaded as long as the game runs, as the local hero's own do,
    /// so their copies stay with the character. It is dressed while it is switched off, as Player Color chooses once
    /// for each object, by the sheet that it first draws from, whether to colour it in the local player's colour.
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="copy">The copy, switched off.</param>
    public void DressHeroCopy(GameObject playerObject, GameObject copy) {
        if (!_figureCopies.TryGetValue(playerObject, out var copies)) {
            return;
        }

        try {
            copy.GetComponentsInChildren(true, DressedAnimators);
            foreach (var animator in DressedAnimators) {
                var library = animator.Library;
                if (library != null && !copies.Own.Contains(library) &&
                    FigureLibrary(playerObject, copies, library, SheetRole.Effects) is { } own) {
                    animator.Library = own;
                }
            }

            // The picture that a sprite shows until its first clip starts, or for good on one that plays none. A
            // sprite that is switched off only takes the sheet, which it draws from as it wakes
            copy.GetComponentsInChildren(true, DressedSprites);
            foreach (var sprite in DressedSprites) {
                var sheet = sprite.Collection;
                if (sheet == null || copies.Own.Contains(sheet) ||
                    HeroSheet(playerObject, copies, sheet) is not { } own) {
                    continue;
                }

                if (sprite.gameObject.activeInHierarchy) {
                    sprite.SetSprite(own, sprite.spriteId);
                } else {
                    sprite.Collection = own;
                }
            }
        } finally {
            DressedAnimators.Clear();
            DressedSprites.Clear();
        }
    }

    /// <summary>
    /// The character's own copy of a sheet of sprites that stays loaded as long as the game runs, as the local hero's
    /// own do, made the first time it is needed and dressed in what the player wears; or null for a sheet that could
    /// not be copied, which is not tried again. The copy stays with the character, as the copies of the local hero's
    /// own sheets do.
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="copies">What was copied for the character.</param>
    /// <param name="sheet">The original sheet.</param>
    private tk2dSpriteCollectionData? HeroSheet(
        GameObject playerObject,
        FigureCopies copies,
        tk2dSpriteCollectionData sheet
    ) {
        if (copies.Objects.TryGetValue(sheet, out var known)) {
            return known as tk2dSpriteCollectionData;
        }

        if (copies.Failed.Contains(sheet)) {
            return null;
        }

        try {
            var copy = CopyUtil.SmartCopySpriteCollection(sheet, playerObject, copies.Objects);
            var copiedSheets = new Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> { [copy] = sheet };
            copies.TakeSheets(copiedSheets, ofTheHero: true);
            _skinManager.AddSheets(playerObject, copiedSheets, SheetRole.Effects);
            return copy;
        } catch (Exception e) {
            Logger.Warn($"Could not copy the sheet of sprites '{sheet.name}' for the character of another player: " +
                        e.Message);
            copies.Failed.Add(sheet);
            return null;
        }
    }

    /// <summary>
    /// The character's own copy of a library of clips, made the first time it is needed, with the sheets of sprites
    /// that it draws from dressed in what the player wears; or null for a library that could not be copied, which is
    /// not tried again. The libraries of the local hero stay loaded, and their copies stay with the character; a
    /// library of a figure of a room goes with its room, and its copy goes after it (see <see cref="RoomCopy"/>).
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="copies">What was copied for the character.</param>
    /// <param name="library">The original library.</param>
    /// <param name="role">What it is played for: by the character itself, by the copies of the player's effects, both
    /// libraries of the local hero, or by a figure of a room.</param>
    private tk2dSpriteAnimation? FigureLibrary(
        GameObject playerObject,
        FigureCopies copies,
        tk2dSpriteAnimation library,
        SheetRole role
    ) {
        if (copies.Libraries.TryGetValue(library, out var copy) && copy != null) {
            return copy;
        }

        var ofTheHero = role != SheetRole.Room;

        if (copies.Failed.Contains(library)) {
            return null;
        }

        try {
            var copiedSheets = new Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData>();
            if (ofTheHero) {
                copy = CopyUtil.SmartCopySpriteAnimation(library, playerObject, copiedSheets, copies.Objects);
                copies.CopiedWith[library] = copies.Objects;
            } else {
                var roomCopy = new RoomCopy(library, copies.HeroSheets);
                copy = CopyUtil.SmartCopySpriteAnimation(library, playerObject, null, roomCopy.Objects);
                roomCopy.Took(copy, copiedSheets);
                copies.RoomCopies.Add(roomCopy);
                copies.CopiedWith[library] = roomCopy.Objects;
            }

            copies.Libraries[library] = copy;
            copies.Own.Add(copy);
            copies.TakeSheets(copiedSheets, ofTheHero);
            _skinManager.AddSheets(playerObject, ofTheHero ? SheetsOf(copy, copiedSheets) : copiedSheets, role);
        } catch (Exception e) {
            Logger.Warn($"Could not copy the library '{library.name}' for the character of another player: " +
                        e.Message);
            copies.Failed.Add(library);
            return null;
        }

        if (!ofTheHero) {
            LetGoOfGoneRoomCopies(playerObject, copies);
        }

        return copy;
    }

    /// <summary>
    /// The copied sheets of sprites that a copied library of clips draws from, each with its original: a copy of a
    /// library of the local hero reuses the copies of the sheets that came before it, and is given all of them.
    /// </summary>
    /// <param name="library">The copied library.</param>
    /// <param name="copiedSheets">The copied sheets, by their copies.</param>
    private static Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> SheetsOf(
        tk2dSpriteAnimation library,
        Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> copiedSheets
    ) {
        var drawn = new Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData>();
        foreach (var clip in library.clips ?? []) {
            foreach (var frame in clip?.frames ?? []) {
                var sheet = frame?.spriteCollection;
                if (sheet != null && !drawn.ContainsKey(sheet) && copiedSheets.TryGetValue(sheet, out var original)) {
                    drawn[sheet] = original;
                }
            }
        }

        return drawn;
    }

    /// <summary>
    /// Lets go of what was copied for the character of another player from rooms that are gone: their libraries and
    /// sheets of sprites came with the room and went with it, and the next visit brings new ones, which are copied
    /// anew. The art of skins loaded for their atlases alone goes too. It runs once a new copy has taken its atlases,
    /// so that the art of an atlas that the new room has as well stays rather than being loaded again.
    /// </summary>
    /// <param name="playerObject">The object that draws the character.</param>
    /// <param name="copies">What was copied for the character.</param>
    private void LetGoOfGoneRoomCopies(GameObject playerObject, FigureCopies copies) {
        for (var i = copies.RoomCopies.Count - 1; i >= 0; i--) {
            var roomCopy = copies.RoomCopies[i];
            if (roomCopy.Original != null) {
                continue;
            }

            copies.RoomCopies.RemoveAt(i);

            var sheets = new List<tk2dSpriteCollectionData>();
            foreach (var (original, copy) in roomCopy.Sheets) {
                sheets.Add(copy);
                if (copies.Sheets.TryGetValue(original, out var kept) && kept == copy) {
                    copies.Sheets.Remove(original);
                }
            }

            _skinManager.RemoveSheets(playerObject, sheets);
            foreach (var sheet in sheets) {
                copies.Own.Remove(sheet);
                if (sheet != null) {
                    Object.Destroy(sheet.gameObject);
                }
            }

            foreach (var material in roomCopy.Materials) {
                if (material != null) {
                    Object.Destroy(material);
                }
            }

            if (roomCopy.Original is tk2dSpriteAnimation library) {
                copies.Libraries.Remove(library);
                copies.CopiedWith.Remove(library);
            }

            if (roomCopy.Library != null) {
                copies.Own.Remove(roomCopy.Library);
                Object.Destroy(roomCopy.Library);
            }
        }

        copies.Failed.RemoveWhere(original => original == null);
    }

    /// <summary>
    /// What was copied for the character of another player: every original with its copy, and the libraries of clips
    /// copied so far.
    /// </summary>
    private sealed class FigureCopies {
        /// <summary>
        /// The copies of the local hero's libraries and of everything they draw from, by their originals, which stay as
        /// long as the character: the hero's own art stays loaded.
        /// </summary>
        public readonly Dictionary<object, object> Objects = new();

        /// <summary>
        /// The copies of the libraries copied so far, by their originals.
        /// </summary>
        public readonly Dictionary<tk2dSpriteAnimation, tk2dSpriteAnimation> Libraries = new();

        /// <summary>
        /// What each library was copied with, by its original: <see cref="Objects"/>, or a room's own copies, which
        /// hold the copies of its clips.
        /// </summary>
        public readonly Dictionary<tk2dSpriteAnimation, Dictionary<object, object>> CopiedWith = new();

        /// <summary>
        /// The copy of each sheet of sprites copied so far, by its original.
        /// </summary>
        public readonly Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> Sheets = new();

        /// <summary>
        /// The copies of the local hero's own sheets, by their originals, which the libraries of rooms draw from too.
        /// </summary>
        public readonly Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> HeroSheets = new();

        /// <summary>
        /// What was copied from rooms, which goes after its room (see <see cref="LetGoOfGoneRoomCopies"/>).
        /// </summary>
        public readonly List<RoomCopy> RoomCopies = [];

        /// <summary>
        /// The libraries and sheets that could not be copied, which are not tried again.
        /// </summary>
        public readonly HashSet<Object> Failed = [];

        /// <summary>
        /// The libraries and sheets copied for the character, which are its own already: a copy of one of them would
        /// wear nothing, as a skin names the sheets that it dresses by the names of the originals.
        /// </summary>
        public readonly HashSet<Object> Own = [];

        /// <summary>
        /// Notes copied sheets of sprites, each copy with its original.
        /// </summary>
        public void TakeSheets(
            Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> copiedSheets,
            bool ofTheHero
        ) {
            foreach (var pair in copiedSheets) {
                Sheets[pair.Value] = pair.Key;
                Own.Add(pair.Key);
                if (ofTheHero) {
                    HeroSheets[pair.Value] = pair.Key;
                }
            }
        }
    }

    /// <summary>
    /// A library or a sheet of sprites of a room, or of a creature in it, copied for the character of another player,
    /// with everything that was copied for it alone. What it draws from of the local hero's own sheets it draws from
    /// the character's copies of them, which stay.
    /// </summary>
    private sealed class RoomCopy {
        /// <summary>
        /// The original library or sheet, which goes with its room.
        /// </summary>
        public readonly Object Original;

        /// <summary>
        /// Everything copied for it, by original, beginning with the character's copies of the local hero's sheets.
        /// </summary>
        public readonly Dictionary<object, object> Objects = new();

        /// <summary>
        /// The originals that <see cref="Objects"/> began with, which are not its own.
        /// </summary>
        private readonly HashSet<object> _shared = [];

        /// <summary>
        /// The copy of the library, or null for a sheet copied alone.
        /// </summary>
        public tk2dSpriteAnimation? Library;

        /// <summary>
        /// The sheets copied for it alone, with their originals.
        /// </summary>
        public readonly List<(tk2dSpriteCollectionData Original, tk2dSpriteCollectionData Copy)> Sheets = [];

        /// <summary>
        /// The materials copied for it alone.
        /// </summary>
        public readonly List<Material> Materials = [];

        public RoomCopy(Object original, Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData>? heroSheets) {
            Original = original;
            if (heroSheets == null) {
                return;
            }

            foreach (var pair in heroSheets) {
                Objects[pair.Key] = pair.Value;
                _shared.Add(pair.Key);
            }
        }

        /// <summary>
        /// Notes what was copied for it alone, and gives the sheets among that, each copy with its original.
        /// </summary>
        public void Took(
            tk2dSpriteAnimation? library,
            Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> copiedSheets
        ) {
            Library = library;
            foreach (var pair in Objects) {
                if (_shared.Contains(pair.Key)) {
                    continue;
                }

                if (pair.Key is tk2dSpriteCollectionData original && pair.Value is tk2dSpriteCollectionData copy) {
                    Sheets.Add((original, copy));
                    copiedSheets[copy] = original;
                } else if (pair.Value is Material material) {
                    Materials.Add(material);
                }
            }
        }
    }

    /// <summary>
    /// Add a name to the given player container object.
    /// </summary>
    /// <param name="playerContainer">The GameObject for the player container.</param>
    /// <param name="name">The username that the object should have.</param>
    /// <param name="team">The team that the player is on.</param>
    public void AddNameToPlayer(GameObject playerContainer, string name, Team team = Team.None) {
        // Create a name object to set the username to, slightly above the player object
        var nameObject = playerContainer.FindGameObjectInChildren(UsernameObjectName);

        if (!nameObject) {
            nameObject = CreateUsername(playerContainer);
        }

        var textMeshObject = nameObject.GetComponent<TextMeshPro>();
        if (textMeshObject == null) {
            textMeshObject = nameObject.AddComponent<TextMeshPro>();
        }

        if (textMeshObject) {
            // The font is picked from the name and not once when the object is made, because the name arrives after
            // it and a partner who calls themselves something the game's own font cannot draw would be a row of
            // empty boxes over their head.
            var drawn = name.ToUpper();
            textMeshObject.font = Ui.Resources.FontManager.PickInGameNameFont(drawn);
            textMeshObject.text = drawn;
            ChangeNameColor(textMeshObject, team);
            DrawOwnNameApart(textMeshObject, playerContainer);
        }

        nameObject.SetActive(_serverSettings.DisplayNames);
    }

    /// <summary>
    /// Callback method for when a player team update is received.
    /// </summary>
    /// <param name="self">Whether this update is for the local player.</param>
    /// <param name="team">The new team of the player.</param>
    /// <param name="playerId">The ID of the player that has updated their team if <paramref name="self"/> is true.
    /// </param>
    public void OnPlayerTeamUpdate(bool self, Team team, ushort playerId = 0) {
        if (self) {
            Logger.Debug($"Received PlayerTeamUpdate for local player: {Enum.GetName(typeof(Team), team)}");

            UpdateLocalPlayerTeam(team);
            return;
        }

        Logger.Debug($"Received PlayerTeamUpdate for ID: {playerId}, team: {Enum.GetName(typeof(Team), team)}");

        UpdatePlayerTeam(playerId, team);
    }

    /// <summary>
    /// Reset the local player's team to be None and reset all existing player names and hit-boxes.
    /// </summary>
    public void ResetAllTeams() {
        UpdateLocalPlayerTeam(Team.None);

        foreach (var id in _playerData.Keys) {
            UpdatePlayerTeam(id, Team.None);
        }
    }

    /// <summary>
    /// Update the team of a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="team">The team that the player should have.</param>
    private void UpdatePlayerTeam(ushort id, Team team) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Tried to update team for ID {id} while player data did not exists");
            return;
        }

        // Store the old team for invoking the event later
        var oldTeam = playerData.Team;

        // Update the team in the player data
        playerData.Team = team;

        if (!playerData.IsInLocalScene || playerData.PlayerContainer == null) {
            return;
        }

        // Get the name object and update the color based on the new team
        var nameObject = playerData.PlayerContainer.FindGameObjectInChildren(UsernameObjectName);
        if (nameObject == null) {
            throw new Exception("Name object could not be found in player container while updating player team");
        }

        var textMeshObject = nameObject.GetComponent<TextMeshPro>();

        ChangeNameColor(textMeshObject, team);

        if (oldTeam != team) {
            PlayerTeamChangeEvent?.Invoke(playerData, team);
        }
    }

    /// <summary>
    /// Update the team for the local player.
    /// </summary>
    /// <param name="team">The new team of the local player.</param>
    private void UpdateLocalPlayerTeam(Team team) {
        var oldTeam = LocalPlayerTeam;
        LocalPlayerTeam = team;

        var nameObject = HeroController.instance.gameObject.FindGameObjectInChildren(UsernameObjectName);
        if (nameObject == null) {
            throw new Exception(
                "Name object could not be found in hero controller object while updating local player team"
            );
        }

        var textMeshObject = nameObject.GetComponent<TextMeshPro>();
        ChangeNameColor(textMeshObject, team);

        if (oldTeam != team) {
            LocalPlayerTeamChangeEvent?.Invoke(team);
        }
    }

    /// <summary>
    /// Get the team of a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <returns>The team of the player.</returns>
    public Team GetPlayerTeam(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            return Team.None;
        }

        return playerData.Team;
    }

    /// <summary>
    /// Get the type of crest of a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <returns>The crest type of the player.</returns>
    public CrestType GetPlayerCrestType(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            return CrestType.Hunter;
        }

        return playerData.CrestType;
    }

    /// <summary>
    /// What the player's own hero wears through skin mods, as the text that tells the other games.
    /// </summary>
    public string LocalSkinLook() => _skinManager.LocalLook();

    /// <summary>
    /// Raised when what the player's own hero wears may have changed while playing.
    /// </summary>
    public event Action? LocalSkinLookChanged {
        add => _skinManager.LocalLookChanged += value;
        remove => _skinManager.LocalLookChanged -= value;
    }

    /// <summary>
    /// What the game said in the chat about what it can't show of the other players, by player, so that it says each
    /// thing once.
    /// </summary>
    private readonly HashSet<string> _reportedSkinProblems = [];

    /// <summary>
    /// Callback for what the players who were here before this game connected wear, which came with the server's info.
    /// </summary>
    public void OnSkinsKnown() {
        foreach (var playerData in _playerData.Values) {
            ReportSkinProblems(playerData);
        }

        WantSkins();
    }

    /// <summary>
    /// Callback method for when another player says what they wear through skin mods.
    /// </summary>
    /// <param name="playerId">The ID of the player.</param>
    /// <param name="skin">What they wear, as the text of a SkinLook.</param>
    public void OnPlayerSkinUpdate(ushort playerId, string skin) {
        if (!_playerData.TryGetValue(playerId, out var playerData)) {
            Logger.Debug($"Received what player {playerId} wears, but they are not known");
            return;
        }

        Logger.Info(
            $"Player {playerData.Username} wears: " +
            (skin.Length == 0 ? "the game's own look" : skin.Replace('\n', ';'))
        );

        playerData.Skin = skin;

        // Dressed first, so that the art of what the character wore before is let go below if nobody wears it
        if (playerData.IsInLocalScene && playerData.PlayerObject != null) {
            _skinManager.Dress(playerData.PlayerObject, skin);
        }

        ReportSkinProblems(playerData);
        WantSkins();
    }

    /// <summary>
    /// Callback for a player leaving, whose skins no one may wear any more, and whose ID a player who joins later may
    /// get: what was said in the chat about the one who left may be said of that one again.
    /// </summary>
    /// <param name="playerId">The ID of the player who left.</param>
    public void OnPlayerLeftSkins(ushort playerId) {
        var saidOf = $"{playerId}\n";
        _reportedSkinProblems.RemoveWhere(said => said.StartsWith(saidOf, StringComparison.Ordinal));
        WantSkins();
    }

    /// <summary>
    /// Callback for the server changing whether players can wear skins: every character is dressed again, and what this
    /// game can't show of what the players wear, said only once skins are on, is said now.
    /// </summary>
    public void OnSkinsAllowedChanged() {
        _skinManager.RedrawAll();

        foreach (var playerData in _playerData.Values) {
            ReportSkinProblems(playerData);
        }
    }

    /// <summary>
    /// Says what the other players wear, so the art of their skins loads before their characters show.
    /// </summary>
    private void WantSkins() {
        var looks = new List<string>();
        foreach (var playerData in _playerData.Values) {
            looks.Add(playerData.Skin);
        }

        _skinManager.Want(looks);
    }

    /// <summary>
    /// Says in the chat what this game can't show of what the given player wears, each thing once: the player sees
    /// them otherwise than they see themselves, and should know why.
    /// </summary>
    private void ReportSkinProblems(ClientPlayerData playerData) {
        foreach (var problem in _skinManager.Problems(playerData.Username, playerData.Skin)) {
            if (_reportedSkinProblems.Add($"{playerData.Id}\n{problem}")) {
                UiManager.InternalChatBox.AddMessage(problem);
                Logger.Info(problem);
            }
        }
    }

    /// <summary>
    /// Draws the name over this player's own head apart from the other, if they asked for that.
    ///
    /// Two players of the same character are the same handful of pixels in a fight, and the name is the only thing
    /// that differs - but it is set in a thin face that a dark, busy screen swallows. So the weight and the colour
    /// are each their own choice: one is about being able to read the name at all, the other about telling at a
    /// glance which of the two is you.
    ///
    /// Only ever this player's own name, and only on this machine. The other player's name is left exactly as it
    /// was, which is what makes the rule work: the one that stands out is you.
    /// </summary>
    /// <param name="textMeshObject">The name to draw.</param>
    /// <param name="playerContainer">What the name hangs over, which says whose it is.</param>
    private void DrawOwnNameApart(TextMeshPro textMeshObject, GameObject playerContainer) {
        var hero = HeroController.instance;
        if (hero == null || hero.gameObject != playerContainer) {
            return;
        }

        DrawAsOwnName(textMeshObject, _modSettings);
    }

    /// <summary>
    /// Draws a name the way this player wants their own drawn: heavier and in their colour, as they chose. Also for
    /// the name on the figure that shows this on the settings page (Ui.SittingPreview).
    /// </summary>
    /// <param name="textMeshObject">The name to draw, in the colour it has otherwise.</param>
    /// <param name="modSettings">The settings of this machine.</param>
    internal static void DrawAsOwnName(TextMeshPro textMeshObject, ModSettings modSettings) {
        textMeshObject.outlineWidth = modSettings.BoldOwnName ? BoldNameOutline : NameOutline;
        textMeshObject.fontStyle = modSettings.BoldOwnName ? FontStyles.Bold : FontStyles.Normal;

        if (modSettings.ColourOwnName &&
            OwnNameColours.TryGetValue(modSettings.OwnNameColour ?? "", out var colour)) {
            textMeshObject.color = colour;
        }
    }

    /// <summary>
    /// Draws the name over this player's own head again, after they changed how they want it drawn.
    /// </summary>
    public void RedrawOwnName() {
        var hero = HeroController.instance;
        if (hero == null || hero.gameObject == null) {
            return;
        }

        var nameObject = hero.gameObject.FindGameObjectInChildren(UsernameObjectName);
        if (nameObject == null || !nameObject.TryGetComponent<TextMeshPro>(out var textMeshObject)) {
            return;
        }

        // Put back what the team says first, so that turning the colour off returns the name to what it would have
        // been rather than leaving the last colour on it
        ChangeNameColor(textMeshObject, LocalPlayerTeam);
        DrawOwnNameApart(textMeshObject, hero.gameObject);
    }

    /// <summary>
    /// Change the color of a TextMeshPro object according to the team.
    /// </summary>
    /// <param name="textMeshObject">The TextMeshPro object representing the name.</param>
    /// <param name="team">The team that the name should be colored after.</param>
    private void ChangeNameColor(TextMeshPro textMeshObject, Team team) {
        switch (team) {
            case Team.Moss:
                textMeshObject.color = new Color(0f / 255f, 150f / 255f, 0f / 255f);
                break;
            case Team.Hive:
                textMeshObject.color = new Color(200f / 255f, 150f / 255f, 0f / 255f);
                break;
            case Team.Grimm:
                textMeshObject.color = new Color(250f / 255f, 50f / 255f, 50f / 255f);
                break;
            case Team.Lifeblood:
                textMeshObject.color = new Color(50f / 255f, 150f / 255f, 200f / 255f);
                break;
            default:
                textMeshObject.color = Color.white;
                break;
        }
    }

    /// <summary>
    /// Remove the name from the local player.
    /// </summary>
    private void RemoveNameFromLocalPlayer() {
        if (HeroController.instance) {
            RemoveNameFromPlayer(HeroController.instance.gameObject);
        }
    }

    /// <summary>
    /// Remove the name of a given player container.
    /// </summary>
    /// <param name="playerContainer">The GameObject for the player container.</param>
    private void RemoveNameFromPlayer(GameObject playerContainer) {
        // Get the name object
        var nameObject = playerContainer.FindGameObjectInChildren(UsernameObjectName);

        // Deactivate it if it exists
        if (nameObject) {
            nameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Callback method for when the server settings are updated.
    /// </summary>
    /// <param name="displayNamesChanged">Whether the display names setting changed.</param>
    public void OnServerSettingsUpdated(bool displayNamesChanged) {
        if (displayNamesChanged) {
            foreach (var playerData in _playerData.Values) {
                if (playerData.PlayerContainer == null) {
                    continue;
                }

                var nameObject = playerData.PlayerContainer.FindGameObjectInChildren(UsernameObjectName);
                if (nameObject) {
                    nameObject.SetActive(_serverSettings.DisplayNames);
                }
            }

            var localPlayerObject = HeroController.instance.gameObject;
            if (localPlayerObject) {
                var nameObject = localPlayerObject.FindGameObjectInChildren(UsernameObjectName);
                if (nameObject) {
                    nameObject.SetActive(_serverSettings.DisplayNames);
                }
            }
        }
    }

    /// <summary>
    /// Create a new username object and add it as a child of a player container.
    /// </summary>
    /// <param name="playerContainer">The player container to add the username object as a child of.</param>
    /// <returns>The new GameObject that was created for the username.</returns>
    internal static GameObject CreateUsername(GameObject playerContainer) {
        var nameObject = new GameObject(UsernameObjectName) {
            transform = {
                position = playerContainer.transform.position + Vector3.up * 1.5f
            }
        };
        nameObject.transform.SetParent(playerContainer.transform);
        nameObject.transform.localScale = new Vector3(0.25f, 0.25f, nameObject.transform.localScale.z);

        nameObject.AddComponent<KeepWorldScalePositive>();

        // Add a TextMeshPro component to it, so we can render text
        var textMeshObject = nameObject.AddComponent<TextMeshPro>();
        textMeshObject.text = UsernameObjectName;
        textMeshObject.alignment = TextAlignmentOptions.Center;
        textMeshObject.font = Ui.Resources.FontManager.InGameNameFont;
        textMeshObject.fontSize = 22;
        textMeshObject.outlineWidth = 0.2f;
        textMeshObject.outlineColor = Color.black;

        nameObject.transform.SetParent(playerContainer.transform);

        return nameObject;
    }
}
