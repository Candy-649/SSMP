using System;
using System.Collections.Generic;
using System.Reflection;
using EnvironmentTypes = GlobalEnums.EnvironmentTypes;
using MonoMod.RuntimeDetour;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// The dust that the game raises under the hero as they land, jump, run or dash back. It makes that dust anew where the
/// hero stands, rather than switching on a part of the hero (see <see cref="HeroChildEffects"/>), so other players never
/// saw it. Each time the local hero raises some, the other players are told which, and what the ground under the hero
/// is, and their games raise the same dust under that player's character. That dust makes no sound, makes no noise
/// that creatures hear, and doesn't shake the screen or the controller: those are for the player who landed. The dust of
/// running is said again for as long as it runs, so that a lost word that it stopped leaves it running no longer than a
/// moment.
/// </summary>
internal class HeroDust : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroDust Instance = new();

    /// <summary>
    /// The kinds of dust, as they go over the network: new ones go at the end.
    /// </summary>
    private enum Kind : byte {
        SoftLand,
        HardLand,
        Jump,
        RunStart,
        RunStop,
        BackDash
    }

    /// <summary>
    /// How long the dust raised for another player stays at most, in seconds, for dust that doesn't end itself.
    /// </summary>
    private const float LongestDust = 3f;

    /// <summary>
    /// How long the dust of running stays after it stopped, in seconds, so that what it raised settles first.
    /// </summary>
    private const float RunDustSettles = 2f;

    /// <summary>
    /// How often the local hero's dust of running is said again while it runs, in seconds.
    /// </summary>
    internal const float RunSaidEvery = 0.5f;

    /// <summary>
    /// How long the dust of running of another player runs on after it was last said, in seconds: a lost word that it
    /// stopped leaves it running no longer than this.
    /// </summary>
    internal const float RunHeardFor = 1.5f;

    /// <summary>
    /// The FSM of the camera that shakes the screen, which a hard landing tells to shake.
    /// </summary>
    private const string CameraShakeFsmName = "CameraShake";

    /// <summary>
    /// Sends the effect info of the dust to the other players, or null before the local hero is watched.
    /// </summary>
    private static Action<byte[]>? _send;

    /// <summary>
    /// The hooks that see the local hero raise dust, which stay for as long as the game runs.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// Whether the hooks were made, or tried to be.
    /// </summary>
    private static bool _hooked;

    /// <summary>
    /// How deep the switching on of dust for another player goes now, while which the screen doesn't shake.
    /// </summary>
    private static int _raisingForOther;

    /// <summary>
    /// The local hero's dust of running that runs now. The hero can have more than one at a time: their own, and the
    /// one that some moves raise.
    /// </summary>
    private static readonly HashSet<RunEffects> HeroRuns = [];

    /// <summary>
    /// Starts watching the local hero raise dust, which may be a new hero or the same one again. This can run while the
    /// hero is still being made, like <see cref="HeroChildEffects.Watch"/>.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of dust to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        _send = send;
        HeroRuns.Clear();
        if (!hero.TryGetComponent<HeroDustWatcher>(out _)) {
            hero.gameObject.AddComponent<HeroDustWatcher>();
        }

        if (_hooked) {
            return;
        }

        _hooked = true;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        AddHook(
            typeof(HeroController).GetMethod(nameof(HeroController.PlaySoftLandingEffect), flags, null, [], null),
            new Action<Action<HeroController>, HeroController>(OnSoftLanding)
        );
        AddHook(
            typeof(HeroController).GetMethod("SpawnSoftLandingPrefab", flags, null, [], null),
            new Action<Action<HeroController>, HeroController>(OnSoftLanding)
        );
        AddHook(
            typeof(HeroController).GetMethod(nameof(HeroController.DoHardLandingEffectNoHit), flags, null, [], null),
            new Action<Action<HeroController>, HeroController>(OnHardLanding)
        );
        AddHook(
            typeof(JumpEffects).GetMethod(
                nameof(JumpEffects.Play),
                flags,
                null,
                [typeof(GameObject), typeof(Vector2), typeof(Vector3)],
                null
            ),
            new Action<Action<JumpEffects, GameObject, Vector2, Vector3>, JumpEffects, GameObject, Vector2, Vector3>(
                OnJumpDust
            )
        );
        AddHook(
            typeof(RunEffects).GetMethod(
                nameof(RunEffects.StartEffect),
                flags,
                null,
                [typeof(bool), typeof(bool)],
                null
            ),
            new Action<Action<RunEffects, bool, bool>, RunEffects, bool, bool>(OnRunDustStart)
        );
        AddHook(
            typeof(RunEffects).GetMethod(nameof(RunEffects.Stop), flags, null, [], null),
            new Action<Action<RunEffects>, RunEffects>(OnRunDustStop)
        );
        AddHook(
            typeof(DashEffect).GetMethod(nameof(DashEffect.Play), flags, null, [typeof(GameObject)], null),
            new Action<Action<DashEffect, GameObject>, DashEffect, GameObject>(OnBackDashDust)
        );
        AddHook(
            typeof(PlayMakerFSM).GetMethod(nameof(PlayMakerFSM.SendEvent), flags, null, [typeof(string)], null),
            new Action<Action<PlayMakerFSM, string>, PlayMakerFSM, string>(OnSendEvent)
        );
        AddHook(
            typeof(EnviroRegionListener).GetProperty(nameof(EnviroRegionListener.IsSprinting), flags)?.GetGetMethod(true),
            new Func<Func<EnviroRegionListener, bool>, EnviroRegionListener, bool>(OnIsSprinting)
        );
    }

    /// <summary>
    /// Hooks a method of the game, logging a method that is not there.
    /// </summary>
    private static void AddHook(MethodInfo? method, Delegate detour) {
        if (method == null) {
            Logger.Error($"Could not find the method for {detour.Method.Name}; that dust will not show to other players");
            return;
        }

        try {
            Hooks.Add(new Hook(method, detour));
        } catch (Exception e) {
            Logger.Error($"Could not hook the method for {detour.Method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// The local hero lands softly, which raises a little dust where they stand.
    /// </summary>
    private static void OnSoftLanding(Action<HeroController> orig, HeroController self) {
        orig(self);
        Send([(byte) Kind.SoftLand, (byte) PlayerData.instance.environmentType]);
    }

    /// <summary>
    /// The local hero lands hard, which raises a lot of dust where they stand.
    /// </summary>
    private static void OnHardLanding(Action<HeroController> orig, HeroController self) {
        orig(self);
        Send([(byte) Kind.HardLand, (byte) PlayerData.instance.environmentType]);
    }

    /// <summary>
    /// The dust of a jump starts, which is the local hero's when they are its owner. What it raises depends on how the
    /// hero moves.
    /// </summary>
    private static void OnJumpDust(
        Action<JumpEffects, GameObject, Vector2, Vector3> orig,
        JumpEffects self,
        GameObject owner,
        Vector2 velocity,
        Vector3 offset
    ) {
        orig(self, owner, velocity, offset);

        if (IsLocalHero(owner)) {
            var info = new byte[10];
            info[0] = (byte) Kind.Jump;
            info[1] = GetGround(owner);
            BitConverter.GetBytes(velocity.x).CopyTo(info, 2);
            BitConverter.GetBytes(velocity.y).CopyTo(info, 6);
            Send(info);
        }
    }

    /// <summary>
    /// Dust of running starts, which is the local hero's when the game starts it as the hero's and it runs with them.
    /// </summary>
    private static void OnRunDustStart(
        Action<RunEffects, bool, bool> orig,
        RunEffects self,
        bool isHeroEffect,
        bool isHeroSprintmasterEffect
    ) {
        orig(self, isHeroEffect, isHeroSprintmasterEffect);

        var hero = HeroController.SilentInstance;
        if (isHeroEffect && hero != null && self.transform.IsChildOf(hero.transform)) {
            HeroRuns.Add(self);
            TellRun(self);
        }
    }

    /// <summary>
    /// Dust of running stops, which is told when it was the local hero's.
    /// </summary>
    private static void OnRunDustStop(Action<RunEffects> orig, RunEffects self) {
        var wasHeros = HeroRuns.Remove(self);
        orig(self);

        if (wasHeros) {
            TellRunStopped(self.GetInstanceID());
        }
    }

    /// <summary>
    /// The dust of a dash back starts, which is the local hero's when they are its owner.
    /// </summary>
    private static void OnBackDashDust(Action<DashEffect, GameObject> orig, DashEffect self, GameObject owner) {
        orig(self, owner);

        if (IsLocalHero(owner)) {
            Send([(byte) Kind.BackDash, 0]);
        }
    }

    /// <summary>
    /// Sends an event to an FSM, except to the one that shakes the screen while dust is raised for another player.
    /// </summary>
    private static void OnSendEvent(Action<PlayMakerFSM, string> orig, PlayMakerFSM self, string eventName) {
        if (_raisingForOther > 0 && self.FsmName == CameraShakeFsmName) {
            return;
        }

        orig(self, eventName);
    }

    /// <summary>
    /// Whether what dust of running reads its ground from says that the one it runs with sprints. For the dust of
    /// another player, that is what the player said; the game would say that they always do.
    /// </summary>
    private static bool OnIsSprinting(Func<EnviroRegionListener, bool> orig, EnviroRegionListener self) {
        return self.TryGetComponent<OtherPlayerGround>(out var ground) ? ground.Sprinting : orig(self);
    }

    /// <summary>
    /// Says again which of the local hero's dust of running runs, and on what ground, for <see cref="HeroDustWatcher"/>.
    /// Dust that ended without being stopped, as when the room goes, is told to have stopped.
    /// </summary>
    internal static void SayRunsAgain() {
        if (HeroRuns.Count == 0) {
            return;
        }

        List<RunEffects>? ended = null;
        foreach (var run in HeroRuns) {
            if (run == null || !run.isActive || !run.gameObject.activeInHierarchy) {
                (ended ??= []).Add(run!);
            } else {
                TellRun(run);
            }
        }

        if (ended == null) {
            return;
        }

        foreach (var run in ended) {
            HeroRuns.Remove(run);
            TellRunStopped(run.GetInstanceID());
        }
    }

    /// <summary>
    /// Whether an object is the local hero.
    /// </summary>
    private static bool IsLocalHero(GameObject? gameObject) {
        var hero = HeroController.SilentInstance;
        return hero != null && gameObject == hero.gameObject;
    }

    /// <summary>
    /// What the ground under an object is, by what the game reads for its dust.
    /// </summary>
    private static byte GetGround(GameObject gameObject) {
        var listener = gameObject.GetComponentInParent<EnviroRegionListener>();
        return listener != null
            ? (byte) listener.CurrentEnvironmentType
            : (byte) PlayerData.instance.environmentType;
    }

    /// <summary>
    /// Tells the other players that dust of running of the local hero runs, on what ground, and of which kind: that of
    /// running or that of sprinting.
    /// </summary>
    private static void TellRun(RunEffects run) {
        var id = (ushort) run.GetInstanceID();
        Send([(byte) Kind.RunStart, GetGround(run.gameObject), (byte) id, (byte) (id >> 8), (byte) run.currentRunType]);
    }

    /// <summary>
    /// Tells the other players that dust of running of the local hero stopped.
    /// </summary>
    private static void TellRunStopped(int instanceId) {
        var id = (ushort) instanceId;
        Send([(byte) Kind.RunStop, 0, (byte) id, (byte) (id >> 8)]);
    }

    /// <summary>
    /// Sends effect info to the other players, if the local hero is watched; what throws here must not stop the game's
    /// own dust.
    /// </summary>
    private static void Send(byte[] info) {
        try {
            _send?.Invoke(info);
        } catch (Exception e) {
            Logger.Warn($"Could not tell other players about dust: {e.Message}");
        }
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not { Length: >= 2 } || HeroController.SilentInstance is not { } hero) {
            return;
        }

        var ground = (EnvironmentTypes) effectInfo[1];
        try {
            switch ((Kind) effectInfo[0]) {
                case Kind.SoftLand:
                    RaiseLanding(hero.softLandingEffectPrefab, playerObject, ground);
                    break;
                case Kind.HardLand:
                    RaiseLanding(hero.hardLandingEffectPrefab, playerObject, ground);
                    break;
                case Kind.Jump when effectInfo.Length >= 10:
                    RaiseJump(
                        hero.jumpEffectPrefab != null ? hero.jumpEffectPrefab.gameObject : null,
                        playerObject,
                        ground,
                        new Vector2(BitConverter.ToSingle(effectInfo, 2), BitConverter.ToSingle(effectInfo, 6))
                    );
                    break;
                case Kind.RunStart when effectInfo.Length >= 5:
                    KeepRun(
                        hero.runEffectPrefab != null ? hero.runEffectPrefab.gameObject : null,
                        playerObject,
                        ground,
                        ReadRunId(effectInfo),
                        effectInfo[4]
                    );
                    break;
                case Kind.RunStop when effectInfo.Length >= 4:
                    if (playerObject.TryGetComponent<RunDustCopy>(out var copy)) {
                        EndRun(copy, ReadRunId(effectInfo));
                    }

                    break;
                case Kind.BackDash:
                    RaiseBackDash(hero.backDashPrefab != null ? hero.backDashPrefab.gameObject : null, playerObject);
                    break;
            }
        } catch (Exception e) {
            Logger.Warn($"Could not raise dust of kind {effectInfo[0]} for another player: {e}");
        }
    }

    /// <summary>
    /// Stops the dust of running of another player, for a character that leaves the room.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void Reset(GameObject playerObject) {
        if (!playerObject.TryGetComponent<RunDustCopy>(out var copy)) {
            return;
        }

        foreach (var id in new List<ushort>(copy.Runs.Keys)) {
            EndRun(copy, id);
        }
    }

    /// <summary>
    /// What the other player knows dust of running by, from the effect info.
    /// </summary>
    private static ushort ReadRunId(byte[] effectInfo) {
        return (ushort) (effectInfo[2] | effectInfo[3] << 8);
    }

    /// <summary>
    /// Raises the dust of a landing under another player's character, picked for their ground: the dust reads the
    /// ground from the local player's data, which is theirs for that moment.
    /// </summary>
    private static void RaiseLanding(GameObject? prefab, GameObject playerObject, EnvironmentTypes ground) {
        var dust = MakeDust(prefab, playerObject.transform.position);
        if (dust == null) {
            return;
        }

        var playerData = PlayerData.instance;
        var localGround = playerData.environmentType;
        playerData.environmentType = ground;
        try {
            SwitchOn(dust);
        } finally {
            playerData.environmentType = localGround;
        }

        Object.Destroy(dust, LongestDust);
    }

    /// <summary>
    /// Raises the dust of a jump under another player's character, which follows them as it goes, picked for their
    /// ground.
    /// </summary>
    private static void RaiseJump(
        GameObject? prefab,
        GameObject playerObject,
        EnvironmentTypes ground,
        Vector2 velocity
    ) {
        var dust = MakeDust(prefab, playerObject.transform.position);
        if (dust == null || !dust.TryGetComponent<JumpEffects>(out var jump)) {
            DestroyDust(dust);
            return;
        }

        // It reads the ground from what it follows
        var jumpGround = OtherPlayerGround.ForJumps(playerObject.transform);
        jumpGround.Set(ground, false);
        SwitchOn(dust);
        jump.Play(jumpGround.gameObject, velocity, Vector3.zero);
        Object.Destroy(dust, LongestDust);
    }

    /// <summary>
    /// Starts dust of running under another player's character, which runs with them until it stops, or keeps it running
    /// if it runs already. Dust whose ground or kind changed starts anew, as the player's own changed.
    /// </summary>
    private static void KeepRun(
        GameObject? prefab,
        GameObject playerObject,
        EnvironmentTypes ground,
        ushort id,
        byte runType
    ) {
        if (!playerObject.TryGetComponent<RunDustCopy>(out var copy)) {
            copy = playerObject.AddComponent<RunDustCopy>();
        }

        var sprinting = runType == 1;
        if (copy.Runs.TryGetValue(id, out var running)) {
            if (running.Dust != null && running.Dust.isActive && running.Ground != null &&
                running.Ground.Is(ground, sprinting)) {
                running.HeardAt = Time.unscaledTime;
                return;
            }

            EndRun(copy, id);
        }

        var dust = MakeDust(prefab, playerObject.transform.position);
        if (dust == null || !dust.TryGetComponent<RunEffects>(out var run)) {
            DestroyDust(dust);
            return;
        }

        // Each runs on a ground of its own, since what they read their kind from is where they run
        var runGround = OtherPlayerGround.Make(playerObject.transform, "Other Player Run Ground");
        runGround.Set(ground, sprinting);
        dust.transform.SetParent(runGround.transform, false);
        dust.transform.localPosition = Vector3.zero;
        SwitchOn(dust);

        // Not the hero's own, which would read the local hero's state and play the sounds of their sprint
        run.StartEffect(false, false);
        PlayHeroParts(run);

        copy.Runs[id] = new RunDustCopy.Run { Dust = run, Ground = runGround, HeardAt = Time.unscaledTime };
    }

    /// <summary>
    /// Plays the parts of dust of running that the game plays only for the hero's own, which the copy isn't.
    /// </summary>
    private static void PlayHeroParts(RunEffects run) {
        var runTypes = run.runTypes;
        var runType = (int) run.currentRunType;
        if (runTypes == null || runType < 0 || runType >= runTypes.Length ||
            runTypes[runType]?.AllEffects is not { } heroParts) {
            return;
        }

        foreach (var particles in heroParts) {
            if (particles != null) {
                particles.Play(true);
            }
        }
    }

    /// <summary>
    /// Stops dust of running of another player's character, which then settles and goes.
    /// </summary>
    internal static void EndRun(RunDustCopy copy, ushort id) {
        if (!copy.Runs.Remove(id, out var run)) {
            return;
        }

        if (run.Dust != null) {
            // Stopping lets go of the ground it ran on, so the ground goes alone
            run.Dust.Stop();
            Object.Destroy(run.Dust.gameObject, RunDustSettles);
        }

        if (run.Ground != null) {
            Object.Destroy(run.Ground.gameObject);
        }
    }

    /// <summary>
    /// Raises the dust of a dash back under another player's character, turned the way the game turns it for the hero.
    /// </summary>
    private static void RaiseBackDash(GameObject? prefab, GameObject playerObject) {
        var dust = MakeDust(prefab, playerObject.transform.position);
        if (dust == null || !dust.TryGetComponent<DashEffect>(out var dash)) {
            DestroyDust(dust);
            return;
        }

        var scale = playerObject.transform.localScale;
        dust.transform.localScale = new Vector3(-scale.x, scale.y, scale.z);
        SwitchOn(dust);
        dash.Play(playerObject);
        Object.Destroy(dust, LongestDust);
    }

    /// <summary>
    /// Makes dust from the game's own, switched off, at a place, without what on it makes sound, makes noise that
    /// creatures hear, or shakes the camera or the controller. It is made rather than taken from the game's pool, so
    /// that none of that goes missing from the local hero's own dust; its end, which would put it back in the pool,
    /// destroys it.
    /// </summary>
    /// <returns>The dust, or null if there is no such dust.</returns>
    private static GameObject? MakeDust(GameObject? prefab, Vector3 position) {
        if (prefab == null) {
            return null;
        }

        // Made inside a holder that is switched off, so that nothing on it starts before it is ready
        var holder = new GameObject("Partner Dust Holder");
        holder.SetActive(false);
        var dust = Object.Instantiate(prefab, holder.transform, false);
        dust.SetActive(false);
        dust.transform.SetParent(null, false);
        Object.Destroy(holder);

        dust.name = prefab.name + " (Other Player)";
        dust.transform.position = position;
        foreach (var audio in dust.GetComponentsInChildren<AudioSource>(true)) {
            audio.mute = true;
        }

        // Switched off rather than removed, like on the copies of the hero's parts: switched off, they do nothing
        foreach (var sound in dust.GetComponentsInChildren<PlayRandomAudioEvent>(true)) {
            sound.enabled = false;
        }

        foreach (var noise in dust.GetComponentsInChildren<NoiseMaker>(true)) {
            noise.enabled = false;
        }

        foreach (var vibration in dust.GetComponentsInChildren<VibrationPlayer>(true)) {
            vibration.enabled = false;
        }

        foreach (var shaker in dust.GetComponentsInChildren<CameraShakeOnEnable>(true)) {
            shaker.enabled = false;
        }

        foreach (var shaker in dust.GetComponentsInChildren<CameraControlAnimationEvents>(true)) {
            shaker.enabled = false;
        }

        return dust;
    }

    /// <summary>
    /// Switches dust raised for another player on, without shaking the screen.
    /// </summary>
    private static void SwitchOn(GameObject dust) {
        _raisingForOther++;
        try {
            dust.SetActive(true);
        } finally {
            _raisingForOther--;
        }
    }

    /// <summary>
    /// Destroys dust that could not be raised.
    /// </summary>
    private static void DestroyDust(GameObject? dust) {
        if (dust != null) {
            Object.Destroy(dust);
        }
    }
}

/// <summary>
/// The ground that dust of another player's character reads, for <see cref="HeroDust"/>: what the player said it is, and
/// whether they sprint. It is a part of the character, so that the dust that follows it reads it, and its listener is
/// never started: started, it would read the ground where it is, and tell everything in the room that the ground changed,
/// which starts the local hero's own dust of running anew.
/// </summary>
internal class OtherPlayerGround : MonoBehaviour {
    /// <summary>
    /// The name of the ground that the dust of jumps reads, of which a character has one.
    /// </summary>
    private const string JumpGroundName = "Other Player Jump Ground";

    /// <summary>
    /// What the dust reads the ground from.
    /// </summary>
    [NonSerialized]
    public EnviroRegionListener? Listener;

    /// <summary>
    /// Whether the player sprints, which dust of running reads for its kind.
    /// </summary>
    [NonSerialized]
    public bool Sprinting;

    /// <summary>
    /// Makes a ground on a character.
    /// </summary>
    public static OtherPlayerGround Make(Transform character, string name) {
        var groundObject = new GameObject(name);
        groundObject.transform.SetParent(character, false);

        var listener = groundObject.AddComponent<EnviroRegionListener>();
        listener.enabled = false;

        var ground = groundObject.AddComponent<OtherPlayerGround>();
        ground.Listener = listener;
        return ground;
    }

    /// <summary>
    /// The ground of a character that the dust of jumps reads, made the first time.
    /// </summary>
    public static OtherPlayerGround ForJumps(Transform character) {
        var existing = character.Find(JumpGroundName);
        if (existing != null && existing.TryGetComponent<OtherPlayerGround>(out var ground)) {
            return ground;
        }

        return Make(character, JumpGroundName);
    }

    /// <summary>
    /// Makes the ground what the player said it is: set on the listener directly, which tells nobody.
    /// </summary>
    public void Set(EnvironmentTypes ground, bool sprinting) {
        Sprinting = sprinting;
        if (Listener != null) {
            Listener.overrideEnvironment = true;
            Listener.overrideEnvironmentType = ground;
        }
    }

    /// <summary>
    /// Whether the ground is the one the player said, with them sprinting or not.
    /// </summary>
    public bool Is(EnvironmentTypes ground, bool sprinting) {
        return Sprinting == sprinting && Listener != null && Listener.overrideEnvironment &&
               Listener.overrideEnvironmentType == ground;
    }
}

/// <summary>
/// The dust of running that runs with another player's character, for <see cref="HeroDust"/>, by what the player knows
/// each by. Dust that isn't said again for a while stops.
/// </summary>
internal class RunDustCopy : MonoBehaviour {
    /// <summary>
    /// One dust of running.
    /// </summary>
    internal class Run {
        /// <summary>
        /// The dust, which the game may destroy when it ends.
        /// </summary>
        public RunEffects? Dust;

        /// <summary>
        /// The ground it runs on.
        /// </summary>
        public OtherPlayerGround? Ground;

        /// <summary>
        /// When the player last said that it runs, in unscaled time.
        /// </summary>
        public float HeardAt;
    }

    /// <summary>
    /// The dust that runs, by what the player knows it by.
    /// </summary>
    [NonSerialized]
    public readonly Dictionary<ushort, Run> Runs = new();

    private void Update() {
        if (Runs.Count == 0) {
            return;
        }

        List<ushort>? quiet = null;
        foreach (var pair in Runs) {
            if (pair.Value.Dust == null || Time.unscaledTime - pair.Value.HeardAt > HeroDust.RunHeardFor) {
                (quiet ??= []).Add(pair.Key);
            }
        }

        if (quiet == null) {
            return;
        }

        foreach (var id in quiet) {
            HeroDust.EndRun(this, id);
        }
    }

    private void OnDestroy() {
        foreach (var run in Runs.Values) {
            if (run.Dust != null) {
                Destroy(run.Dust.gameObject);
            }
        }

        Runs.Clear();
    }
}

/// <summary>
/// Says the local hero's dust of running again every so often, for <see cref="HeroDust"/>.
/// </summary>
internal class HeroDustWatcher : MonoBehaviour {
    /// <summary>
    /// When to say it again, in unscaled time.
    /// </summary>
    private float _sayAt;

    private void LateUpdate() {
        if (Time.unscaledTime < _sayAt) {
            return;
        }

        _sayAt = Time.unscaledTime + HeroDust.RunSaidEvery;
        HeroDust.SayRunsAgain();
    }
}
