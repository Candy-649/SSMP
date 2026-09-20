using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op rules for arenas, where gates close and waves of enemies attack (<see cref="BattleScene"/>).
/// The players in a room share its battle. Whoever walks in first starts it, but the gates only close for the players
/// who walked in: a player outside can still come in to help, and can't leave again until the battle is won.
/// The scene host controls the enemies, so its game counts them, starts the waves and wins the battle, and the games of
/// the other players follow it. If the scene host dies or leaves, the next scene host continues from the last state it
/// received. After every player left the room, the battle starts over like it does alone.
/// A battle that the scene host had already won before is not fought again, so other players can walk through it.
/// </summary>
internal class ArenaCoop {
    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// How long, in seconds, a player behind closed gates waits for the scene host to start the battle before the gates
    /// open again.
    /// </summary>
    private const float StartRequestTimeout = 5f;

    /// <summary>
    /// Event that opens the gates of an arena.
    /// </summary>
    private const string OpenGatesEvent = "BG OPEN";

    /// <summary>
    /// The events that an arena and its waves send their enemies to set them up and then to send them at the players,
    /// in the order in which the game sends them.
    /// </summary>
    private static readonly string[] EnemyWakeEvents = ["BATTLE", "BATTLE START"];

    /// <summary>
    /// How many wake events in a row one enemy is given, so that an enemy which only waits for the next event after
    /// hearing one still comes out, without ever looping.
    /// </summary>
    private const int MaxWakeEvents = 4;

    /// <summary>
    /// How long, in seconds, between the looks over a running battle for an enemy that never heard its wave start.
    /// </summary>
    private const float WakeCheckInterval = 2f;

    /// <summary>
    /// Names of the arena methods that count enemies or end waves. Players who follow the scene host skip them, since
    /// the scene host sends them the number of enemies and the waves.
    /// </summary>
    private static readonly string[] ProgressMethodNames =
        ["WaveEnd", "DecrementEnemy", "DecrementBigEnemy", "DoCheckEnemiesNew"];

    /// <summary>
    /// Reflected field that tells whether the battle of an arena was won.
    /// </summary>
    private static readonly FieldInfo? CompletedField = typeof(BattleScene).GetField("completed", InstanceFlags);

    /// <summary>
    /// Reflected field with the number of enemies that an arena counts as remaining.
    /// </summary>
    private static readonly FieldInfo? CurrentEnemiesField =
        typeof(BattleScene).GetField("currentEnemies", InstanceFlags);

    /// <summary>
    /// Reflected field with the number of frames before an arena turns off the enemies of its waves after loading.
    /// </summary>
    private static readonly FieldInfo? LoopsUntilDeactivateField =
        typeof(BattleScene).GetField("loopsUntilDeactivate", InstanceFlags);

    /// <summary>
    /// Reflected field with the waves of an arena.
    /// </summary>
    private static readonly FieldInfo? WavesField = typeof(BattleScene).GetField("waves", InstanceFlags);

    /// <summary>
    /// Reflected field with the camera locks that an arena turns on when it locks in the local player.
    /// </summary>
    private static readonly FieldInfo? CamLocksField = typeof(BattleScene).GetField("camLocks", InstanceFlags);

    /// <summary>
    /// Reflected field with the box trigger that starts the battle of an arena.
    /// </summary>
    private static readonly FieldInfo? BoxColliderField =
        typeof(BattleScene).GetField("boxCollider2D", InstanceFlags);

    /// <summary>
    /// Reflected field with the polygon trigger that starts the battle of an arena.
    /// </summary>
    private static readonly FieldInfo? PolygonColliderField =
        typeof(BattleScene).GetField("polygonCollider2D", InstanceFlags);

    /// <summary>
    /// Reflected method that starts the battle of an arena.
    /// </summary>
    private static readonly MethodInfo? StartBattleMethod =
        typeof(BattleScene).GetMethod("StartBattle", InstanceFlags);

    /// <summary>
    /// Reflected method that closes the gates of an arena and turns on its camera locks.
    /// </summary>
    private static readonly MethodInfo? LockInBattleMethod =
        typeof(BattleScene).GetMethod("LockInBattle", InstanceFlags);

    /// <summary>
    /// Reflected coroutine method that starts a wave of an arena.
    /// </summary>
    private static readonly MethodInfo? StartWaveMethod = typeof(BattleScene).GetMethod("StartWave", InstanceFlags);

    /// <summary>
    /// Reflected coroutine method that wins the battle of an arena.
    /// </summary>
    private static readonly MethodInfo? EndBattleMethod = typeof(BattleScene).GetMethod("EndBattle", InstanceFlags);

    /// <summary>
    /// Reflected method that sends an event to the gates of an arena.
    /// </summary>
    private static readonly MethodInfo? SendEventToChildrenMethod =
        typeof(BattleScene).GetMethod("SendEventToChildren", InstanceFlags);

    /// <summary>
    /// Reflected method that turns the FSMs and enemy detection of a wave's enemies on or off.
    /// </summary>
    private static readonly MethodInfo? WaveSetActiveMethod = typeof(BattleWave).GetMethod("SetActive", InstanceFlags);

    /// <summary>
    /// Delegate for the original <c>BattleWave.WaveStarted</c>, which adds the enemies of a wave to a count.
    /// </summary>
    private delegate void WaveStartedOrig(BattleWave self, bool activateEnemies, ref int currentEnemies);

    /// <summary>
    /// Delegate for the hook on <c>BattleWave.WaveStarted</c>.
    /// </summary>
    private delegate void WaveStartedHook(
        WaveStartedOrig orig,
        BattleWave self,
        bool activateEnemies,
        ref int currentEnemies
    );

    /// <summary>
    /// The net client for sending arena states to other players.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other connected players, by player ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The entity manager, which knows whether the local player is the scene host.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// Whether the server synchronises entities. Without it, every player fights their own enemies and arenas run
    /// separately.
    /// </summary>
    private readonly Func<bool> _isFullSynchronisation;

    /// <summary>
    /// The co-op state of the arenas in the current scene.
    /// </summary>
    private readonly Dictionary<BattleScene, ArenaState> _arenas = new();

    /// <summary>
    /// The hooks on arenas and waves.
    /// </summary>
    private readonly List<Hook> _hooks = [];

    /// <summary>
    /// The arena that this game is changing for another player, or null.
    /// </summary>
    private BattleScene? _remoteTarget;

    /// <summary>
    /// The arena whose trigger the local player is walking into, or null.
    /// </summary>
    private BattleScene? _heroTriggerTarget;

    public ArenaCoop(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        EntityManager entityManager,
        Func<bool> isFullSynchronisation
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _entityManager = entityManager;
        _isFullSynchronisation = isFullSynchronisation;
    }

    /// <summary>
    /// Registers the hooks for arena co-op.
    /// </summary>
    public void RegisterHooks() {
        if (CompletedField == null || CurrentEnemiesField == null || LoopsUntilDeactivateField == null ||
            WavesField == null || CamLocksField == null || BoxColliderField == null || PolygonColliderField == null ||
            StartBattleMethod == null || LockInBattleMethod == null || StartWaveMethod == null ||
            EndBattleMethod == null || SendEventToChildrenMethod == null || WaveSetActiveMethod == null) {
            Logger.Error("Could not find the arena members of the game; arena co-op is disabled");
            return;
        }

        AddHook(typeof(BattleScene), "StartBattle", new Action<Action<BattleScene>, BattleScene>(OnStartBattle));
        AddHook(
            typeof(BattleScene),
            "OnTriggerEnter2D",
            new Action<Action<BattleScene, Collider2D>, BattleScene, Collider2D>(OnTriggerEnter2D)
        );
        AddHook(typeof(BattleScene), "LockInBattle", new Action<Action<BattleScene>, BattleScene>(OnLockInBattle));
        AddHook(
            typeof(BattleScene),
            "StartWave",
            new Func<Func<BattleScene, int, IEnumerator>, BattleScene, int, IEnumerator>(OnStartWave)
        );
        AddHook(
            typeof(BattleScene),
            "EndBattle",
            new Func<Func<BattleScene, bool, IEnumerator>, BattleScene, bool, IEnumerator>(OnEndBattle)
        );
        foreach (var methodName in ProgressMethodNames) {
            AddHook(typeof(BattleScene), methodName, new Action<Action<BattleScene>, BattleScene>(OnProgress));
        }

        AddHook(typeof(BattleScene), "Update", new Action<Action<BattleScene>, BattleScene>(OnUpdate));
        AddHook(typeof(BattleWave), "WaveStarted", new WaveStartedHook(OnWaveStarted));

        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>
    /// Disposes the hooks for arena co-op.
    /// </summary>
    public void DeregisterHooks() {
        foreach (var hook in _hooks) {
            hook.Dispose();
        }

        _hooks.Clear();

        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        _arenas.Clear();
        _remoteTarget = null;
    }

    /// <summary>
    /// Creates a hook, logging an error instead of throwing if the method does not exist or can't be hooked.
    /// </summary>
    private void AddHook(Type type, string methodName, Delegate detour) {
        var method = type.GetMethod(methodName, InstanceFlags);
        if (method == null) {
            Logger.Error($"Could not find {type.Name}.{methodName}; hook was not registered");
            return;
        }

        try {
            _hooks.Add(new Hook(method, detour));
        } catch (Exception e) {
            Logger.Error($"Could not hook {type.Name}.{methodName}:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when another player in the scene sends the state of an arena.
    /// </summary>
    /// <param name="update">The BattleSceneUpdate packet data.</param>
    public void OnBattleSceneUpdate(BattleSceneUpdate update) {
        if (!_isFullSynchronisation() || SceneManager.GetActiveScene().name != update.SceneName) {
            return;
        }

        var battleScene = FindBattleScene(update.Path);
        if (update.Status == BattleSceneStatus.StartRequest) {
            OnStartRequest(update.Path, battleScene);
            return;
        }

        // Only players who follow the scene host take its state
        if (battleScene == null || (_entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost)) {
            return;
        }

        var state = GetState(battleScene);
        if (state.HostUpdate?.Status != update.Status) {
            Logger.Info($"Arena '{state.Path}' is {update.Status} for the scene host");
        }

        state.HostUpdate = update;
        state.HostUpdatePending = true;
        state.RequestTime = null;

        if (IsFollower()) {
            ApplyHostUpdate(battleScene, state);
        }
    }

    /// <summary>
    /// Sends the state of the arenas in the scene to a player who entered it, if the local player is the scene host.
    /// </summary>
    public void OnPlayerEnterScene() {
        if (!_isFullSynchronisation() || !_entityManager.IsSceneHost) {
            return;
        }

        var battleScenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var battleScene in battleScenes) {
            var state = GetState(battleScene);
            if (IsCompleted(battleScene)) {
                Send(state.Path, BattleSceneStatus.AlreadyWon);
            } else if (state.Started) {
                SendRunning(battleScene, state);
            }
        }
    }

    /// <summary>
    /// Continues the battles of the previous scene host after the local player became the scene host.
    /// </summary>
    public void OnBecomeSceneHost() {
        foreach (var pair in _arenas.ToList()) {
            var battleScene = pair.Key;
            var state = pair.Value;

            state.HostUpdate = null;
            state.HostUpdatePending = false;
            state.RequestTime = null;

            if (battleScene == null || IsCompleted(battleScene)) {
                continue;
            }

            if (state.Started) {
                Logger.Info($"Continuing arena '{state.Path}' as the scene host");
                WakeTheEnemiesThatNeverHeardTheirWave(battleScene, state, true);
                SendRunning(battleScene, state);
            } else if (state.LockedIn) {
                // The previous scene host left before it started the battle, while the local player is behind the gates
                Logger.Info($"Starting arena '{state.Path}' as the scene host");
                CallArenaMethod(StartBattleMethod!, battleScene);
            }
        }
    }

    /// <summary>
    /// Starts the battle of an arena for another player who walked into it, if the local player is the scene host.
    /// </summary>
    private void OnStartRequest(string path, BattleScene? battleScene) {
        if (!_entityManager.IsSceneHost) {
            return;
        }

        if (battleScene == null || !battleScene.gameObject.activeInHierarchy) {
            Logger.Info($"Another player walked into arena '{path}', which is not active here");
            Send(path, BattleSceneStatus.Unavailable);
            return;
        }

        var state = GetState(battleScene);
        if (IsCompleted(battleScene)) {
            Send(state.Path, BattleSceneStatus.AlreadyWon);
            return;
        }

        if (!state.Started) {
            Logger.Info($"Another player walked into arena '{state.Path}', starting the battle");
            StartForOtherPlayer(battleScene);
        }

        SendRunning(battleScene, state);
    }

    /// <summary>
    /// Starts the battle of an arena. The scene host, or a player alone in the room, starts it as usual and tells the
    /// other players. A player who follows the scene host only closes their own gates and asks the scene host to start
    /// the battle.
    /// </summary>
    private void OnStartBattle(Action<BattleScene> orig, BattleScene self) {
        var state = GetState(self);
        var isRemote = IsRemoteTarget(self);

        if (isRemote || !IsFollower()) {
            // The battle only starts once. Players who walk in after it started are locked in by OnTriggerEnter2D.
            if (state.Started) {
                return;
            }

            state.Started = true;

            if (isRemote || IsLocalHeroInArena(self)) {
                orig(self);
            } else {
                // Enemies can start a battle when they notice another player, so the gates stay open for the local
                // player until they walk in
                StartForOtherPlayer(self, () => orig(self));
            }

            if (!isRemote) {
                SendRunning(self, state);
            }

            return;
        }

        if (IsCompleted(self) ||
            state.HostUpdate is { Status: BattleSceneStatus.AlreadyWon or BattleSceneStatus.Unavailable }) {
            return;
        }

        LockIn(self, state);

        if (state.HostUpdate == null && state.RequestTime == null) {
            Logger.Info($"Asking the scene host to start arena '{state.Path}'");
            state.RequestTime = Time.unscaledTime;
            Send(state.Path, BattleSceneStatus.StartRequest);
        }
    }

    /// <summary>
    /// Lets the arena start its battle when the local player walks into it, remembering that the local player is the
    /// one who walked in. Locks in the local player if the battle already runs.
    /// </summary>
    private void OnTriggerEnter2D(Action<BattleScene, Collider2D> orig, BattleScene self, Collider2D collision) {
        var isLocalHero = IsLocalHero(collision);
        var previousTarget = _heroTriggerTarget;
        if (isLocalHero) {
            _heroTriggerTarget = self;
        }

        try {
            orig(self, collision);
        } finally {
            _heroTriggerTarget = previousTarget;
        }

        if (!isLocalHero || !_arenas.TryGetValue(self, out var state) || state.LockedIn || IsCompleted(self)) {
            return;
        }

        var isRunning = IsFollower() ? state.HostUpdate?.Status == BattleSceneStatus.Running : state.Started;
        if (isRunning) {
            LockIn(self, state);
        }
    }

    /// <summary>
    /// Closes the gates of an arena for the local player, unless this game starts the battle for another player.
    /// </summary>
    private void OnLockInBattle(Action<BattleScene> orig, BattleScene self) {
        if (IsRemoteTarget(self)) {
            return;
        }

        orig(self);
        GetState(self).LockedIn = true;
    }

    /// <summary>
    /// Starts a wave of an arena. A player who follows the scene host only starts the waves that the scene host sent,
    /// and no wave starts twice. The scene host tells the other players about each wave.
    /// </summary>
    private IEnumerator OnStartWave(Func<BattleScene, int, IEnumerator> orig, BattleScene self, int waveNumber) {
        var state = GetState(self);
        var isRemote = IsRemoteTarget(self);
        if ((!isRemote && IsFollower()) || waveNumber <= state.Wave) {
            return NoRoutine();
        }

        state.Wave = waveNumber;
        var routine = orig(self, waveNumber);

        if (!isRemote) {
            SendRunning(self, state);
        }

        return routine;
    }

    /// <summary>
    /// Wins the battle of an arena. A player who follows the scene host only wins it when the scene host did, and the
    /// scene host tells the players in the room.
    /// </summary>
    private IEnumerator OnEndBattle(Func<BattleScene, bool, IEnumerator> orig, BattleScene self, bool waitExtra) {
        var isRemote = IsRemoteTarget(self);
        if (!isRemote && IsFollower()) {
            return NoRoutine();
        }

        var state = GetState(self);
        if (!state.Ended && !IsCompleted(self)) {
            state.Ended = true;
            state.LockedIn = false;

            if (!isRemote) {
                Logger.Info($"Won arena '{state.Path}'");
                Send(state.Path, BattleSceneStatus.Won);
            }
        }

        return orig(self, waitExtra);
    }

    /// <summary>
    /// Counts enemies or ends a wave, except for a player who follows the scene host, which sends them instead.
    /// </summary>
    private void OnProgress(Action<BattleScene> orig, BattleScene self) {
        if (IsFollower()) {
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Turns on the enemies of a wave. A player who follows the scene host doesn't add them to the count of the arena,
    /// since the scene host sends it.
    /// </summary>
    private void OnWaveStarted(WaveStartedOrig orig, BattleWave self, bool activateEnemies, ref int currentEnemies) {
        if (!IsFollower()) {
            orig(self, activateEnemies, ref currentEnemies);
            return;
        }

        var ignoredEnemies = currentEnemies;
        orig(self, activateEnemies, ref ignoredEnemies);
    }

    /// <summary>
    /// Applies the state of the scene host for a player who follows it. For the scene host, sends the number of enemies
    /// whenever it changes, so that the next scene host can continue the battle, and keeps an eye out for an enemy that
    /// never heard its wave start.
    /// </summary>
    private void OnUpdate(Action<BattleScene> orig, BattleScene self) {
        orig(self);

        if (!_arenas.TryGetValue(self, out var state)) {
            return;
        }

        if (!IsFollower()) {
            CheckForEnemiesThatNeverHeardTheirWave(self, state);
        }

        if (!IsOtherPlayerInScene()) {
            return;
        }

        if (IsFollower()) {
            if (state.HostUpdatePending || state.RequestTime != null) {
                ApplyHostUpdate(self, state);
            }
        } else if (state.Started && !state.Ended && GetCurrentEnemies(self) != state.SentEnemies) {
            SendRunning(self, state);
        }
    }

    /// <summary>
    /// Applies the last state of the scene host to an arena, for a player who follows the scene host.
    /// </summary>
    private void ApplyHostUpdate(BattleScene battleScene, ArenaState state) {
        var update = state.HostUpdate;
        if (update == null) {
            // Without an answer from the scene host, open the gates again instead of trapping the local player
            if (state.RequestTime is { } requestTime && Time.unscaledTime - requestTime > StartRequestTimeout) {
                Logger.Warn($"The scene host did not start arena '{state.Path}', opening the gates");
                state.RequestTime = null;
                Release(battleScene, state);
            }

            return;
        }

        // Wait until the arena is active and turned off the enemies of its waves after loading
        if (!battleScene.gameObject.activeInHierarchy || GetInt(LoopsUntilDeactivateField, battleScene) > 0) {
            return;
        }

        state.HostUpdatePending = false;

        switch (update.Status) {
            case BattleSceneStatus.Running:
                // A battle that the local player already won in their own save has nothing to follow
                if (IsCompleted(battleScene)) {
                    break;
                }

                if (!state.Started) {
                    StartForOtherPlayer(battleScene);
                }

                if (update.Wave > state.Wave) {
                    StartWaveForOtherPlayer(battleScene, state, update.Wave);
                }

                CurrentEnemiesField!.SetValue(battleScene, (int) update.Enemies);
                break;
            case BattleSceneStatus.Won:
                if (!state.Ended && !IsCompleted(battleScene)) {
                    ApplyRemote(battleScene, () => {
                        if (CallArenaMethod(EndBattleMethod!, battleScene, false) is IEnumerator routine) {
                            battleScene.StartCoroutine(routine);
                        }
                    });
                }

                break;
            case BattleSceneStatus.AlreadyWon:
            case BattleSceneStatus.Unavailable:
                if (state.LockedIn) {
                    Release(battleScene, state);
                }

                break;
        }
    }

    /// <summary>
    /// Starts the battle of an arena for another player, leaving the gates of the local player open.
    /// </summary>
    /// <param name="battleScene">The arena.</param>
    /// <param name="start">The action that starts the battle, or null to call <c>StartBattle</c>.</param>
    private void StartForOtherPlayer(BattleScene battleScene, Action? start = null) {
        // Starting the battle turns off the trigger, which the local player still needs to be locked in when they walk in
        var boxCollider = BoxColliderField!.GetValue(battleScene) as Collider2D;
        var polygonCollider = PolygonColliderField!.GetValue(battleScene) as Collider2D;
        var boxEnabled = boxCollider != null && boxCollider.enabled;
        var polygonEnabled = polygonCollider != null && polygonCollider.enabled;

        start ??= () => CallArenaMethod(StartBattleMethod!, battleScene);
        ApplyRemote(battleScene, start);

        if (boxCollider != null) {
            boxCollider.enabled = boxEnabled;
        }

        if (polygonCollider != null) {
            polygonCollider.enabled = polygonEnabled;
        }
    }

    /// <summary>
    /// Starts the wave that the scene host started. Earlier waves that this game skipped get their enemies turned on, in
    /// case some of them are still alive when this game has to run the battle.
    /// </summary>
    private void StartWaveForOtherPlayer(BattleScene battleScene, ArenaState state, int waveNumber) {
        if (WavesField!.GetValue(battleScene) is IList waves) {
            for (var i = state.Wave + 1; i < waveNumber && i < waves.Count; i++) {
                if (waves[i] is BattleWave wave && wave != null) {
                    CallArenaMethod(WaveSetActiveMethod!, wave, true);
                }
            }
        }

        ApplyRemote(battleScene, () => {
            if (CallArenaMethod(StartWaveMethod!, battleScene, waveNumber) is IEnumerator routine) {
                battleScene.StartCoroutine(routine);
            }
        });
    }

    /// <summary>
    /// Looks over the waves that are running for an enemy that is still waiting to hear that its wave started, and
    /// tells it. Nothing in the game would ever end a wave that one of its enemies never came out for, and the enemy
    /// that missed it has no timer to fall back on, so this is the only way back out of it. It runs for the player
    /// whose game runs the enemies, and only while the mod is running the room for both players.
    /// </summary>
    private void CheckForEnemiesThatNeverHeardTheirWave(BattleScene battleScene, ArenaState state) {
        if (!_netClient.IsConnected || !_isFullSynchronisation() || !state.Started || state.Ended ||
            Time.unscaledTime < state.NextWakeCheck || IsCompleted(battleScene)) {
            return;
        }

        state.NextWakeCheck = Time.unscaledTime + WakeCheckInterval;

        WakeTheEnemiesThatNeverHeardTheirWave(battleScene, state, false);
    }

    /// <summary>
    /// Tells the enemies of the waves that are already running to come out, if they are still waiting to hear that
    /// their wave started.
    /// A player who follows the scene host keeps the room's own copy of every enemy switched off, so the wave events
    /// that their own arena sends while it follows are swallowed: PlayMaker drops an event sent to a GameObject that is
    /// off and never delivers it afterwards. An enemy that lies in wait for that event has no timer to fall back on, so
    /// once this game takes the room over, that enemy stays buried, cannot be killed, and the wave never ends for
    /// anyone. Only an enemy whose current state listens for one of the events is told, so an enemy that is already
    /// fighting hears nothing.
    /// </summary>
    /// <param name="battleScene">The arena.</param>
    /// <param name="state">The co-op state of the arena.</param>
    /// <param name="loud">
    /// Whether to say something about an enemy that is switched off, which is worth saying once when the room changes
    /// hands but not over and over while the battle runs.
    /// </param>
    private static void WakeTheEnemiesThatNeverHeardTheirWave(BattleScene battleScene, ArenaState state, bool loud) {
        if (WavesField!.GetValue(battleScene) is not IList waves) {
            return;
        }

        for (var waveNumber = 0; waveNumber <= state.Wave && waveNumber < waves.Count; waveNumber++) {
            if (waves[waveNumber] is not BattleWave wave || wave == null) {
                continue;
            }

            foreach (Transform child in wave.transform) {
                if (!child.gameObject.activeInHierarchy) {
                    // An enemy that was killed is switched off and says nothing; one that still has its health is a
                    // hole that nothing here can fill, since an event never reaches an object that is off
                    var health = child.GetComponent<HealthManager>();
                    if (loud && health != null && health.hp > 0) {
                        Logger.Warn(
                            $"Enemy '{child.name}' of wave {waveNumber} in arena '{state.Path}' is switched off with " +
                            $"{health.hp} health left while its wave runs, so it cannot hear that the wave started"
                        );
                    }

                    continue;
                }

                foreach (var fsm in child.GetComponents<PlayMakerFSM>()) {
                    for (var sent = 0; sent < MaxWakeEvents; sent++) {
                        var waitedFor = GetWakeEventWaitedFor(fsm);
                        if (waitedFor == null) {
                            break;
                        }

                        Logger.Info(
                            $"Enemy '{child.name}' of wave {waveNumber} in arena '{state.Path}' never heard " +
                            $"'{waitedFor}', telling it now"
                        );

                        var stateBefore = fsm.Fsm.ActiveStateName;
                        fsm.Fsm.Event(waitedFor);

                        // An event that PlayMaker only takes in on its own next turn leaves the state where it was, and
                        // sending the same one again would do nothing but fill its queue
                        if (fsm.Fsm.ActiveStateName == stateBefore) {
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The wake event that the current state of an FSM is waiting for, or null if it waits for none of them.
    /// </summary>
    private static string? GetWakeEventWaitedFor(PlayMakerFSM fsm) {
        var activeState = fsm.Fsm?.ActiveState;
        if (activeState == null) {
            return null;
        }

        foreach (var transition in activeState.Transitions ?? []) {
            foreach (var wakeEvent in EnemyWakeEvents) {
                if (transition.EventName == wakeEvent) {
                    return wakeEvent;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the gates of an arena that isn't won yet are closed for the local player.
    /// </summary>
    public bool IsLocalHeroLockedIn() {
        foreach (var pair in _arenas) {
            if (pair.Value.LockedIn && pair.Key != null && !IsCompleted(pair.Key)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Closes the gates of an arena for the local player, if they are not closed yet.
    /// </summary>
    private static void LockIn(BattleScene battleScene, ArenaState state) {
        if (!state.LockedIn) {
            CallArenaMethod(LockInBattleMethod!, battleScene);
        }
    }

    /// <summary>
    /// Opens the gates of an arena for the local player again.
    /// </summary>
    private static void Release(BattleScene battleScene, ArenaState state) {
        state.LockedIn = false;
        CallArenaMethod(SendEventToChildrenMethod!, battleScene, OpenGatesEvent);

        if (CamLocksField!.GetValue(battleScene) is GameObject camLocks && camLocks != null) {
            camLocks.SetActive(false);
        }
    }

    /// <summary>
    /// Runs an action that changes an arena for another player, so that the hooks don't treat it as the local player's.
    /// </summary>
    private void ApplyRemote(BattleScene battleScene, Action action) {
        var previousTarget = _remoteTarget;
        _remoteTarget = battleScene;
        try {
            action();
        } catch (Exception e) {
            Logger.Error($"Could not change arena '{battleScene.name}' for another player:\n{e}");
        } finally {
            _remoteTarget = previousTarget;
        }
    }

    /// <summary>
    /// Whether this game is changing the given arena for another player.
    /// </summary>
    private bool IsRemoteTarget(BattleScene battleScene) {
        return _remoteTarget != null && _remoteTarget == battleScene;
    }

    /// <summary>
    /// Calls a method of the game on an object, logging an error instead of throwing if it fails.
    /// </summary>
    private static object? CallArenaMethod(MethodInfo method, Object target, params object[] parameters) {
        try {
            return method.Invoke(target, parameters);
        } catch (Exception e) {
            Logger.Error($"Could not call {method.Name} on '{target.name}':\n{e}");
            return null;
        }
    }

    /// <summary>
    /// Sends that the battle of an arena runs, with its last wave and number of enemies.
    /// </summary>
    private void SendRunning(BattleScene battleScene, ArenaState state) {
        state.SentEnemies = GetCurrentEnemies(battleScene);
        Send(state.Path, BattleSceneStatus.Running, state.Wave, state.SentEnemies);
    }

    /// <summary>
    /// Sends the state of an arena to the other players in the scene.
    /// </summary>
    private void Send(string path, BattleSceneStatus status, int wave = -1, int enemies = 0) {
        if (!_netClient.IsConnected || !_isFullSynchronisation() || !IsOtherPlayerInScene()) {
            return;
        }

        _netClient.UpdateManager.SetBattleSceneUpdate(
            new BattleSceneUpdate {
                SceneName = SceneManager.GetActiveScene().name,
                Path = path,
                Status = status,
                Wave = (short) Mathf.Clamp(wave, short.MinValue, short.MaxValue),
                Enemies = (short) Mathf.Clamp(enemies, short.MinValue, short.MaxValue)
            }
        );
    }

    /// <summary>
    /// Whether the local player follows the battles of the scene host, because another player in the scene controls
    /// the enemies.
    /// </summary>
    private bool IsFollower() {
        return _netClient.IsConnected && _isFullSynchronisation() && _entityManager.IsSceneRoleDetermined &&
               !_entityManager.IsSceneHost && IsOtherPlayerInScene();
    }

    /// <summary>
    /// Whether another player is in the local scene.
    /// </summary>
    private bool IsOtherPlayerInScene() {
        foreach (var playerData in _playerData.Values) {
            if (playerData.IsInLocalScene) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the co-op state of an arena, creating it if needed.
    /// </summary>
    private ArenaState GetState(BattleScene battleScene) {
        if (!_arenas.TryGetValue(battleScene, out var state)) {
            state = new ArenaState(ScenePath.Get(battleScene.transform));
            _arenas[battleScene] = state;
        }

        return state;
    }

    /// <summary>
    /// Finds the arena with the given path in the loaded scenes, or null if there is none.
    /// </summary>
    private BattleScene? FindBattleScene(string path) {
        foreach (var pair in _arenas) {
            if (pair.Key != null && pair.Value.Path == path) {
                return pair.Key;
            }
        }

        var battleScenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var battleScene in battleScenes) {
            if (GetState(battleScene).Path == path) {
                return battleScene;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a collider that entered a trigger belongs to the local player, in the same way that arenas check it.
    /// </summary>
    private static bool IsLocalHero(Collider2D collision) {
        if (!collision.CompareTag("Player")) {
            return false;
        }

        var heroController = collision.GetComponent<HeroController>();
        return heroController != null && heroController.isHeroInPosition;
    }

    /// <summary>
    /// Whether the local player is in an arena whose battle starts on this game. That is the case when they walked into
    /// its trigger or stand in it. When no other player is in the room, or the arena has no trigger to check, the gates
    /// close like they do alone.
    /// </summary>
    private bool IsLocalHeroInArena(BattleScene battleScene) {
        if (_heroTriggerTarget == battleScene || !IsOtherPlayerInScene()) {
            return true;
        }

        var heroController = HeroController.instance;
        if (heroController == null) {
            return true;
        }

        var heroPosition = (Vector2) heroController.transform.position;
        var hasTrigger = false;
        foreach (var field in new[] { BoxColliderField, PolygonColliderField }) {
            if (field?.GetValue(battleScene) is not Collider2D trigger || trigger == null || !trigger.enabled) {
                continue;
            }

            hasTrigger = true;
            if (trigger.OverlapPoint(heroPosition)) {
                return true;
            }
        }

        return !hasTrigger;
    }

    /// <summary>
    /// Whether the battle of an arena was won.
    /// </summary>
    private static bool IsCompleted(BattleScene battleScene) {
        return CompletedField?.GetValue(battleScene) is true;
    }

    /// <summary>
    /// Gets the number of enemies that an arena counts as remaining.
    /// </summary>
    private static int GetCurrentEnemies(BattleScene battleScene) {
        return GetInt(CurrentEnemiesField, battleScene);
    }

    /// <summary>
    /// Gets the value of an integer field of an arena, or 0 if the field is missing.
    /// </summary>
    private static int GetInt(FieldInfo? field, BattleScene battleScene) {
        return field?.GetValue(battleScene) is int value ? value : 0;
    }

    /// <summary>
    /// A coroutine that does nothing, for waves and battle ends that a player who follows the scene host skips.
    /// </summary>
    private static IEnumerator NoRoutine() {
        yield break;
    }

    /// <summary>
    /// Forgets the arenas of the previous scene.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        _arenas.Clear();
        _remoteTarget = null;
    }

    /// <summary>
    /// The co-op state of an arena in the current scene.
    /// </summary>
    private class ArenaState {
        /// <summary>
        /// The path of the arena in the scene, which finds it in the games of other players.
        /// </summary>
        public readonly string Path;

        /// <summary>
        /// Whether this game started the battle, for the local player or for another player.
        /// </summary>
        public bool Started;

        /// <summary>
        /// Whether the gates are closed for the local player.
        /// </summary>
        public bool LockedIn;

        /// <summary>
        /// The last wave that this game started, or -1 if it didn't start one yet.
        /// </summary>
        public int Wave = -1;

        /// <summary>
        /// Whether this game won the battle.
        /// </summary>
        public bool Ended;

        /// <summary>
        /// The number of enemies that was last sent to other players, or -1 if none was sent.
        /// </summary>
        public int SentEnemies = -1;

        /// <summary>
        /// The last state that the scene host sent, or null.
        /// </summary>
        public BattleSceneUpdate? HostUpdate;

        /// <summary>
        /// Whether the last state of the scene host still has to be applied.
        /// </summary>
        public bool HostUpdatePending;

        /// <summary>
        /// The time at which the local player asked the scene host to start the battle, or null if they are not
        /// waiting for it.
        /// </summary>
        public float? RequestTime;

        /// <summary>
        /// The time from which this game looks over the running waves again for an enemy that never heard its wave
        /// start.
        /// </summary>
        public float NextWakeCheck;

        public ArenaState(string path) {
            Path = path;
        }
    }
}
