using System.Collections.Generic;
using GlobalEnums;
using GlobalSettings;
using SSMP.Api.Client;
using SSMP.Game.Settings;
using SSMP.Hooks;
using SSMP.Networking.Client;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Math_Vector2 = SSMP.Math.Vector2;
using Vector2 = UnityEngine.Vector2;

namespace SSMP.Game.Client;

/// <summary>
/// A class that manages player locations on the in-game map.
/// </summary>
internal class MapManager : IMapManager {
    /// <summary>
    /// The net client instance.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The current server settings.
    /// </summary>
    private readonly ServerSettings _serverSettings;

    /// <summary>
    /// Dictionary containing map icon objects per player ID.
    /// </summary>
    private readonly Dictionary<ushort, PlayerMapEntry> _mapEntries;

    /// <summary>
    /// The last sent map position.
    /// </summary>
    private Vector2 _lastPosition;

    /// <summary>
    /// The value of the last sent whether the map icon was active. If true, we have sent to the server
    /// that we have a map icon active. Otherwise, we have sent to the server that we don't have a map
    /// icon active.
    /// </summary>
    private bool _lastSentMapIcon;

    /// <summary>
    /// Whether we should display the map icons. True if the map is opened, false otherwise.
    /// </summary>
    private bool _displayingIcons;

    /// <summary>
    /// The settings of this player, for the colour of the outline around their own head.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The outline around this player's own head on the map, while connected and once the map has been opened.
    /// </summary>
    private OwnMapIconOutline? _ownOutline;

    public MapManager(NetClient netClient, ServerSettings serverSettings, ModSettings modSettings) {
        _netClient = netClient;
        _serverSettings = serverSettings;
        _modSettings = modSettings;

        _mapEntries = new Dictionary<ushort, PlayerMapEntry>();
    }

    /// <summary>
    /// Initialize the map manager.
    /// </summary>
    public void Initialize() {
        // Register the disconnect event so we can remove map icons
        _netClient.DisconnectEvent += OnDisconnect;
    }

    /// <summary>
    /// Register the hooks needed for map related operations.
    /// </summary>
    public void RegisterHooks() {
        // Register a hero controller update callback, so we can update the map icon position
        EventHooks.HeroControllerUpdate += HeroControllerOnUpdate;

        // Register when the player closes their map, so we can hide the icons
        EventHooks.GameMapCloseQuickMap += OnCloseQuickMap;

        // Register when the player opens their map, which is when the compass position is calculated
        EventHooks.GameMapPositionCompassAndCorpse += OnPositionCompass;
    }

    /// <summary>
    /// Deregister the hooks needed for map related operations.
    /// </summary>
    public void DeregisterHooks() {
        EventHooks.HeroControllerUpdate -= HeroControllerOnUpdate;

        EventHooks.GameMapCloseQuickMap -= OnCloseQuickMap;

        EventHooks.GameMapPositionCompassAndCorpse -= OnPositionCompass;
    }

    /// <summary>
    /// Callback method for the HeroController#Update method.
    /// </summary>
    private void HeroControllerOnUpdate(HeroController heroController) {
        // If we are not connect, we don't have to send anything
        if (!_netClient.IsConnected) {
            return;
        }
    
        // Whether a map icon may be shown at all, looked at before the map position, which is only worked out when it
        // is used. OnlyBroadcastMapIconWithCompass: suppressed unless the compass tool exists and is equipped.
        var iconAllowed = _serverSettings.AlwaysShowMapIcons ||
                          (_serverSettings.OnlyBroadcastMapIconWithCompass && Gameplay.CompassTool &&
                           Gameplay.CompassTool.IsEquipped);
        var newPosition = Vector2.zero;
        var hasMapIcon = iconAllowed && TryGetMapPosition(out newPosition);
    
        if (hasMapIcon != _lastSentMapIcon) {
            _lastSentMapIcon = hasMapIcon;
    
            _netClient.UpdateManager.UpdatePlayerMapIcon(hasMapIcon);
    
            // If we don't have a map icon anymore, we reset the last position so that
            // if we have an icon again, we will immediately also send a map position update
            if (!hasMapIcon) {
                _lastPosition = Vector2.zero;
            }
        }
    
        // If we don't currently have a map icon active or if we are in a scene transition,
        // we don't send map position updates
        if (!hasMapIcon || global::GameManager.instance.IsInSceneTransition) {
            return;
        }
    
        // Only send update if the position changed
        if (newPosition != _lastPosition) {
            var vec2 = new Math_Vector2(newPosition.x, newPosition.y);
    
            _netClient.UpdateManager.UpdatePlayerMapPosition(vec2);
    
            // Update the last position, since it changed
            _lastPosition = newPosition;
        }
    }

    /// <summary>
    /// Try to get the current map position of the local player.
    /// </summary>
    /// <param name="mapPosition">A Vector2 representing the map position or the zero vector if the map position could
    /// not be found.</param>
    /// <returns>True if the map position could be found; false otherwise.</returns>
    private bool TryGetMapPosition(out Vector2 mapPosition) {
        // Set the default value for the map position
        mapPosition = Vector2.zero;

        var hc = HeroController.instance;
        if (hc == null) {
            return false;
        }

        var gm = global::GameManager.instance;
        if (gm == null) {
            return false;
        }

        var gameMap = GetGameMap();
        if (gameMap == null) {
            return false;
        }

        string sceneName;
        MapZone mapZone;
        if (!string.IsNullOrEmpty(gameMap.overriddenSceneName)) {
            sceneName = gameMap.overriddenSceneName;
            mapZone = gameMap.overriddenSceneRegion;
        } else if (MazeController.NewestInstance && !MazeController.NewestInstance.IsCapScene) {
            sceneName = "DustMazeCompassMarker";
            mapZone = gameMap.currentSceneMapZone;
        } else {
            sceneName = gm.sceneName;
            mapZone = gameMap.currentSceneMapZone;
        }

        // The room on the map is looked up only when the map, the room or the area is another than last time:
        // GameMap.GetSceneInfo reads the name of every room of the area on the map until it finds it, a new string
        // each, and this is every frame. Where it is on the map is worked out each time, as GetSceneInfo does.
        if (gameMap != _sceneInfoMap || sceneName != _sceneInfoName || mapZone != _sceneInfoZone ||
            (_sceneInfoObject is not null && _sceneInfoObject == null)) {
            gameMap.GetSceneInfo(sceneName, mapZone, out var scene, out var sceneObj, out _);
            _sceneInfoMap = gameMap;
            _sceneInfoName = sceneName;
            _sceneInfoZone = mapZone;
            _sceneInfoScene = scene;
            _sceneInfoObject = sceneObj;
        }

        var currentScene = _sceneInfoScene;
        var currentSceneObj = _sceneInfoObject;
        var currentScenePos = Vector2.zero;
        if (currentSceneObj != null) {
            var sceneTransform = currentSceneObj.transform;
            var parent = sceneTransform.parent;
            if (parent == null) {
                gameMap.GetSceneInfo(sceneName, mapZone, out currentScene, out currentSceneObj, out currentScenePos);
            } else {
                var local = sceneTransform.localPosition;
                var parentLocal = parent.localPosition;
                currentScenePos = new Vector2(local.x + parentLocal.x, local.y + parentLocal.y);
            }
        }

        var heroPos = (Vector2) hc.transform.position;
        mapPosition = gameMap.GetMapPosition(
            heroPos, 
            currentScene, 
            currentSceneObj, 
            currentScenePos,
            gameMap.currentSceneSize
        );
        return true;
    }

    /// <summary>
    /// The game map that <see cref="_sceneInfoObject"/> was looked up in (see <see cref="TryGetMapPosition"/>).
    /// </summary>
    private GameMap? _sceneInfoMap;

    /// <summary>
    /// The name of the room that <see cref="_sceneInfoObject"/> was looked up for.
    /// </summary>
    private string? _sceneInfoName;

    /// <summary>
    /// The area that <see cref="_sceneInfoObject"/> was looked up in.
    /// </summary>
    private MapZone _sceneInfoZone;

    /// <summary>
    /// The room's part of the map that GameMap.GetSceneInfo found, or null.
    /// </summary>
    private GameMapScene? _sceneInfoScene;

    /// <summary>
    /// The object of the room on the map that GameMap.GetSceneInfo found, or null.
    /// </summary>
    private GameObject? _sceneInfoObject;

    /// <summary>
    /// Update whether the given player has an active map icon.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="hasMapIcon">Whether the player has an active map icon.</param>
    public void UpdatePlayerHasIcon(ushort id, bool hasMapIcon) {
        // If there does not exist an entry for this ID yet, we create it
        if (!_mapEntries.TryGetValue(id, out var mapEntry)) {
            _mapEntries[id] = mapEntry = new PlayerMapEntry();
        }

        if (mapEntry.HasMapIcon) {
            if (!hasMapIcon) {
                // If the player had an active map icon, but we receive that they do not anymore
                // we destroy the map icon object if it exists
                if (mapEntry.GameObject != null) {
                    Object.Destroy(mapEntry.GameObject);
                }
            }
        } else {
            if (hasMapIcon) {
                // If the player did not have an active map icon, but we receive that they do we
                // create an icon
                CreatePlayerIcon(id, mapEntry.Position);
            }
        }

        mapEntry.HasMapIcon = hasMapIcon;
    }

    /// <summary>
    /// Update the map icon of a given player with the given position.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="position">The new position on the map.</param>
    public void UpdatePlayerIcon(ushort id, Math_Vector2 position) {
        // If there does not exist an entry for this id yet, we create it
        if (!_mapEntries.TryGetValue(id, out var mapEntry)) {
            _mapEntries[id] = mapEntry = new PlayerMapEntry();
        }

        // Always store the position in case we later get an active map icon without position
        mapEntry.Position = position;

        // If the player does not have an active map icon
        if (!mapEntry.HasMapIcon) {
            return;
        }

        // Check whether the object still exists
        var mapObject = mapEntry.GameObject;
        if (mapObject == null) {
            CreatePlayerIcon(id, position);
            return;
        }

        // Check if the transform is still valid and otherwise destroy the object
        // This is possible since whenever we receive a new update packet, we
        // will just create a new map icon
        var transform = mapObject.transform;
        if (transform == null) {
            Object.Destroy(mapObject);
            return;
        }

        var unityPosition = new Vector3(
            position.X,
            position.Y,
            transform.localPosition.z
        );

        // Update the position of the player icon
        transform.localPosition = unityPosition;
    }

    /// <summary>
    /// Callback method on the GameMap#CloseQuickMap method.
    /// </summary>
    /// <param name="gameMap">The GameMap instance.</param>
    private void OnCloseQuickMap(GameMap gameMap) {
    
        // We have closed the map, so we can disable the icons
        _displayingIcons = false;
        UpdateMapIconsActive();
    }

    /// <summary>
    /// Callback method on the GameMap#PositionCompass method.
    /// </summary>
    /// <param name="gameMap">The GameMap instance.</param>
    private void OnPositionCompass(GameMap gameMap) {
        // Otherwise, we have opened the map
        _displayingIcons = true;

        // A head that was said to be shown while this game had no map yet, in the menu as the player joined, is made
        // now: it is otherwise only made when its player moves, and one standing still was not on the map
        foreach (var (id, mapEntry) in _mapEntries) {
            if (mapEntry.HasMapIcon && mapEntry.GameObject == null) {
                CreatePlayerIcon(id, mapEntry.Position);
            }
        }

        UpdateMapIconsActive();

        OutlineOwnIcon(gameMap);
    }

    /// <summary>
    /// Put the outline around this player's own head on the map, or bring its colour up to date. The colour is the
    /// one this player picked for their own name, so that the same colour means them in the room and on the map.
    /// </summary>
    /// <param name="gameMap">The GameMap instance.</param>
    private void OutlineOwnIcon(GameMap gameMap) {
        if (!_netClient.IsConnected) {
            return;
        }

        // Turned off in the settings, which only change in the menu, so the next time the map opens is soon enough
        if (!_modSettings.OutlineOwnMapIcon) {
            RemoveOwnOutline();
            return;
        }

        var head = gameMap.compassIcon;
        if (head == null) {
            return;
        }

        var colour = PlayerManager.OwnColour(_modSettings);

        if (_ownOutline != null && _ownOutline.transform.parent == head.transform) {
            _ownOutline.SetColour(colour);
            return;
        }

        RemoveOwnOutline();

        // Only a look, so it never stands in the way of the map opening
        try {
            _ownOutline = OwnMapIconOutline.Attach(head, colour);
        } catch (System.Exception e) {
            Logger.Error($"Could not outline own head on the map:\n{e}");
        }
    }

    /// <summary>
    /// Remove the outline around this player's own head, if there is one.
    /// </summary>
    private void RemoveOwnOutline() {
        if (_ownOutline != null) {
            Object.Destroy(_ownOutline.gameObject);
        }

        _ownOutline = null;
    }

    /// <summary>
    /// Update all existing map icons based on whether they should be active according to server settings.
    /// </summary>
    private void UpdateMapIconsActive() {
        foreach (var mapEntry in _mapEntries.Values) {
            if (mapEntry.HasMapIcon && mapEntry.GameObject != null) {
                mapEntry.GameObject.SetActive(_displayingIcons);
            }
        }
    }

    /// <summary>
    /// Create a map icon for a player and store it in the mapping.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="position">The position of the map icon.</param>
    private void CreatePlayerIcon(ushort id, Math_Vector2 position) {
        if (!_mapEntries.TryGetValue(id, out var mapEntry)) {
            return;
        }

        var gameMap = GetGameMap();
        if (gameMap == null) {
            return;
        }

        var compassIconPrefab = gameMap.compassIcon;
        if (compassIconPrefab == null) {
            Logger.Warn("CompassIcon prefab is null");
            return;
        }

        // Create a new player icon relative to the game map
        var mapIcon = Object.Instantiate(
            compassIconPrefab,
            gameMap.gameObject.transform
        );
        mapIcon.SetActive(_displayingIcons);

        // The copy is taken from this player's own head, outline and all. The outline is only for this player's own
        // head, and the copy is drawn below it and its outline, so that a head over this player's own does not hide it
        var copiedOutline = mapIcon.transform.Find(OwnMapIconOutline.ObjectName);
        if (copiedOutline != null) {
            copiedOutline.gameObject.SetActive(false);
            Object.Destroy(copiedOutline.gameObject);
        }

        if (mapIcon.TryGetComponent<tk2dSprite>(out var sprite) &&
            compassIconPrefab.TryGetComponent<Renderer>(out var ownRenderer)) {
            sprite.SortingOrder = ownRenderer.sortingOrder - OwnMapIconOutline.OtherHeadsBelow;
        }

        var unityPosition = new Vector3(
            position.X,
            position.Y,
            compassIconPrefab.transform.localPosition.z
        );

        // Set the position of the player icon
        mapIcon.transform.localPosition = unityPosition;

        // Remove the bob effect when walking with the map
        Object.Destroy(mapIcon.LocateMyFSM("Mapwalk Bob"));

        // Put it in the list
        mapEntry.GameObject = mapIcon;
    }

    /// <summary>
    /// Remove a map entry for a player. For example, if they disconnect from the server.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    public void RemoveEntryForPlayer(ushort id) {
        if (_mapEntries.TryGetValue(id, out var mapEntry)) {
            if (mapEntry.GameObject != null) {
                Object.Destroy(mapEntry.GameObject);
            }

            _mapEntries.Remove(id);
        }
    }

    /// <summary>
    /// Remove all map icons.
    /// </summary>
    public void RemoveAllIcons() {
        // Destroy all existing map icons
        foreach (var mapEntry in _mapEntries.Values) {
            if (mapEntry.GameObject != null) {
                Object.Destroy(mapEntry.GameObject);
            }
        }
    }

    /// <summary>
    /// Callback method for when the local user disconnects.
    /// </summary>
    private void OnDisconnect() {
        RemoveAllIcons();
        RemoveOwnOutline();

        _mapEntries.Clear();

        // Reset variables to their initial values
        _lastPosition = Vector3.zero;
        _lastSentMapIcon = false;
    }

    /// <summary>
    /// Get a valid instance of the GameMap class.
    /// </summary>
    /// <returns>An instance of GameMap.</returns>
    private GameMap? GetGameMap() {
        var gameManager = global::GameManager.instance;
        if (gameManager == null) {
            return null;
        }

        var gameMap = gameManager.gameMap;
        if (gameMap == null) {
            return null;
        }

        return gameMap;
    }

    /// <inheritdoc />
    public bool TryGetEntry(ushort id, out IPlayerMapEntry? playerMapEntry) {
        var found = _mapEntries.TryGetValue(id, out var entry);
        playerMapEntry = entry;

        return found;
    }

    /// <summary>
    /// An entry for an icon of a player.
    /// </summary>
    private class PlayerMapEntry : IPlayerMapEntry {
        /// <inheritdoc />
        public bool HasMapIcon { get; set; }

        /// <inheritdoc />
        public Math_Vector2 Position { get; set; } = Math_Vector2.Zero;

        /// <summary>
        /// The game object corresponding to the map icon.
        /// </summary>
        public GameObject? GameObject { get; set; }
    }
}
