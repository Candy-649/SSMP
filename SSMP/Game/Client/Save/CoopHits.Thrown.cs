using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The things that creatures throw, which every game knows by the same name (see <see cref="ThrownThings"/>). This part
/// names them as they come out of the pool, rolls their bounces by their names, and sends what the local player does to
/// them to the others like what they do to an object of the room, under the thing's name rather than its place: a
/// strike, a touch of their attack, a touch of their body and a hurt. Each goes with where the thing was and how it
/// moved as it happened, which its twin is put at first, and the thing goes after that player from then on. What a
/// thing does in a replay of the partner's never works on this game's own player (see
/// <see cref="OnThrownTransition"/>).
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// Binding flags for the static methods that are hooked.
    /// </summary>
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// How many states a thrown thing goes through by itself after a replay of the partner's takes it into one, as far
    /// as they are looked through for what works on this game's player (see <see cref="WorksOnThePlayerFrom"/>).
    /// </summary>
    private const int MaxStatesLookedThrough = 8;

    /// <summary>
    /// The fields of each kind of action that hold a text, by kind, which are looked through for one that names the
    /// player's controller (see <see cref="NamesThePlayer"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> TextFields = new();

    /// <summary>
    /// The states of thrown things that a replay of the partner's was kept out of, by the thing and the state, each
    /// written down once.
    /// </summary>
    private static readonly HashSet<string> KeptOutOfStates = [];

    /// <summary>
    /// Whether naming a thrown thing or sending what happened to one threw, which is only logged once.
    /// </summary>
    private bool _thrownFailed;

    /// <summary>
    /// The spawn method of the pool that all others end in.
    /// </summary>
    private delegate GameObject PoolSpawn(
        GameObject prefab,
        Transform parent,
        Vector3 position,
        Quaternion rotation,
        bool stealActiveSpawned
    );

    /// <summary>
    /// Registers the hooks that name thrown things, roll their bounces, send what hurts the local player and keep
    /// replays from working on the local player.
    /// </summary>
    private void RegisterThrownHooks() {
        ThrownThings.CodeOf = CodeOfPlayer;
        ThrownThings.FigureOf = FigureOfThrowTarget;

        AddHook(
            typeof(ObjectPool).GetMethod(
                "Spawn",
                StaticFlags,
                null,
                [typeof(GameObject), typeof(Transform), typeof(Vector3), typeof(Quaternion), typeof(bool)],
                null
            ),
            new Func<PoolSpawn, GameObject, Transform, Vector3, Quaternion, bool, GameObject>(OnPoolSpawn)
        );
        AddHook(
            typeof(ObjectPool).GetMethod(
                "ActivateSpawningObject", StaticFlags, null, [typeof(GameObject), typeof(bool)], null
            ),
            new Action<Action<GameObject, bool>, GameObject, bool>(OnActivateSpawningObject)
        );
        AddHook(
            typeof(ObjectBounce).GetMethod("OnCollisionEnter2D", InstanceFlags, null, [typeof(Collision2D)], null),
            new Action<Action<ObjectBounce, Collision2D>, ObjectBounce, Collision2D>(OnObjectBounce)
        );
        AddHook(
            typeof(DamageHero).GetMethod("SendHeroDamagedEvent", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<DamageHero>, DamageHero>(OnSendHeroDamagedEvent)
        );
        AddHook(
            typeof(Fsm).GetMethod("DoTransition", InstanceFlags, null, [typeof(FsmTransition), typeof(bool)], null),
            new Func<Func<Fsm, FsmTransition, bool, bool>, Fsm, FsmTransition, bool, bool>(OnThrownTransition)
        );
    }

    /// <summary>
    /// Lets go of what <see cref="RegisterThrownHooks"/> set, apart from the hooks themselves.
    /// </summary>
    private static void DeregisterThrownHooks() {
        ThrownThings.CodeOf = null;
        ThrownThings.FigureOf = null;
    }

    /// <summary>
    /// Hook for the spawn of the pool that all others end in, which has what comes out of it named if it is thrown (see
    /// <see cref="ExpectThrown"/>).
    /// </summary>
    private GameObject OnPoolSpawn(
        PoolSpawn orig,
        GameObject prefab,
        Transform parent,
        Vector3 position,
        Quaternion rotation,
        bool stealActiveSpawned
    ) {
        IDisposable? naming = null;
        try {
            naming = ExpectThrown(prefab);
        } catch (Exception e) {
            if (!_thrownFailed) {
                _thrownFailed = true;
                Logger.Error($"Could not name what a creature threw:\n{e}");
            }
        }

        try {
            return orig(prefab, parent, position, rotation, stealActiveSpawned);
        } finally {
            naming?.Dispose();
        }
    }

    /// <summary>
    /// Has what comes out of the pool now named if it is thrown: by a named thrown thing, whose state machines run in
    /// every game alike, or by an action of a creature that this game runs, whose throw goes to the others with the
    /// names. A replayed throw of a creature names what it makes by itself (EntityFsmActions).
    /// </summary>
    /// <param name="prefab">What comes out of the pool.</param>
    /// <returns>What stops the naming once it is out, or null if it is not named.</returns>
    private IDisposable? ExpectThrown(GameObject? prefab) {
        if (prefab == null || !HasMembers) {
            return null;
        }

        var owner = FsmExecutionStack.ExecutingFsm?.GameObject;
        if (owner != null && ThrownThings.TryGetMark(owner, out var parent)) {
            return ThrownThings.CanBeThrown(prefab) ? ThrownThings.ExpectChild(parent, prefab) : null;
        }

        if (ThrownThings.IsExpecting) {
            return null;
        }

        // Only what the action itself throws, as it starts: the action stays the last one of its state for as long as
        // the state lasts, and what else comes out of the pool meanwhile - the effect that a black thread puts on each
        // thing thrown, what the state spawns later - is not a part of the throw that the other game is sent
        var action = FsmExecutionStack.ExecutingAction;
        var thrownPrefab = action switch {
            SpawnObjectFromGlobalPool spawn => spawn.gameObject?.Value,
            FlingObjectsFromGlobalPool fling => fling.gameObject?.Value,
            FlingObjectsFromGlobalPoolVel flingVel => flingVel.gameObject?.Value,
            SpawnRandomObjectsRadialV2 radial => radial.Prefab?.Value,
            _ => null
        };
        if (action == null || thrownPrefab == null || thrownPrefab != prefab || action.Finished ||
            !Entity.Entity.TryGetThrower(action, out var thrower) || !ThrownThings.CanBeThrown(prefab)) {
            return null;
        }

        // The player the creature goes after, which its own actions are pointed at, and otherwise this game's player
        var hero = HeroController.SilentInstance;
        var target = GamePatcher.TargetOf(thrower) ?? (hero != null ? hero.gameObject : null);
        return ThrownThings.ExpectHostThrow(action, prefab, target);
    }

    /// <summary>
    /// Hook for the pool switching on what comes out of it, which gives a thrown thing its name first, so that the
    /// first rolls of its state machines go by it.
    /// </summary>
    private void OnActivateSpawningObject(Action<GameObject, bool> orig, GameObject gameObject, bool alreadyActive) {
        try {
            ThrownThings.OnActivating(gameObject);
        } catch (Exception e) {
            if (!_thrownFailed) {
                _thrownFailed = true;
                Logger.Error($"Could not name what a creature threw:\n{e}");
            }
        }

        orig(gameObject, alreadyActive);
    }

    /// <summary>
    /// Hook for a bounce of a thing that bounces off what it hits, which takes how high it bounces from the name of a
    /// thrown thing, the same in every game.
    /// </summary>
    private static void OnObjectBounce(
        Action<ObjectBounce, Collision2D> orig,
        ObjectBounce self,
        Collision2D collision
    ) {
        if (ThrownThings.Count > 0 && ThrownThings.TryGetMark(self.gameObject, out var mark)) {
            ThrownThings.Rolling(mark.NextBounce(), () => orig(self, collision));
            return;
        }

        orig(self, collision);
    }

    /// <summary>
    /// Hook for a thing that hurt the local player being told of it, which for a thrown thing is sent to the others
    /// after, for their twin of it to be told the same (see <see cref="ReplayHurt"/>): a caltrop that hurt the player
    /// flies off in every game. The thing goes after the player it hurt from then on.
    /// </summary>
    private void OnSendHeroDamagedEvent(Action<DamageHero> orig, DamageHero self) {
        if (ThrownThings.Count == 0 || _isReplaying || !HasMembers ||
            !ThrownThings.TryGetMark(self.gameObject, out var mark)) {
            orig(self);
            return;
        }

        // Its name and where it was as it hurt the player: what it does on it can put it back into the pool, which
        // takes its name off it
        var key = mark.Key;
        var place = ThrownThings.PlaceOf(mark);
        var index = Array.IndexOf(self.GetComponents<DamageHero>(), self);
        TurnThrownToLocalPlayer(self.gameObject, out _);

        orig(self);

        try {
            if (key == null || index is < 0 or > byte.MaxValue || !_netClient.IsConnected || !IsAnyMemberInRoom()) {
                return;
            }

            var update = new CoopHitUpdate {
                Kind = CoopHitKind.ObjectHurtPlayer,
                Scene = SceneManager.GetActiveScene().name,
                Path = ThrownThings.PathOf(key, mark, self.gameObject, place),
                Responder = nameof(DamageHero),
                Index = (byte) index
            };
            SendToRoomMembers(update);
            NoteTraffic(update.Scene, update.Path, "sent a hurt of the player");
        } catch (Exception e) {
            if (!_thrownFailed) {
                _thrownFailed = true;
                Logger.Error($"Could not send a hurt of the player by a thrown thing:\n{e}");
            }
        }
    }

    /// <summary>
    /// Tells the twin here of a thing that a creature threw that it hurt the partner's player, as their game told it:
    /// the same event to its state machine, the same that a script of it hears, put where it was in their game as it
    /// hurt them. What it does on it to the player it hurt does not happen here (see <see cref="OnThrownTransition"/>).
    /// </summary>
    private void ReplayHurt(CoopHitUpdate update) {
        var target = FindPlace(update);
        if (target == null) {
            NotReplayed(update, "hurt", "the thing is not here");
            return;
        }

        var damagers = target.GetComponents<DamageHero>();
        if (update.Index >= damagers.Length) {
            NotReplayed(update, "hurt", $"the thing has only {damagers.Length} parts that hurt here");
            return;
        }

        var damager = damagers[update.Index];
        if (!damager.isActiveAndEnabled || !IsRoomObject(damager)) {
            NotReplayed(update, "hurt", "the part that hurt is switched off here");
            return;
        }

        PutThrownWhereItWas(target, update);

        _isReplaying = true;
        try {
            _gamePatcher.RunAsRemoteHit(damager.SendHeroDamagedEvent);
        } catch (Exception e) {
            Logger.Warn($"Could not replay a hurt of the partner by {update.Path}:\n{e}");
            NoteTraffic(update.Scene, update.Path, "got a hurt of the partner, whose replay failed");
            return;
        } finally {
            _isReplaying = false;
        }

        NoteTraffic(update.Scene, update.Path, "got a hurt of the partner, replayed");
    }

    /// <summary>
    /// Whether something touching an object is the body of the local player, rather than their attack, and the object
    /// a part of a named thrown thing, which then hears of it through <see cref="OnLocalTouchOfThrown"/>. Asked of
    /// every touch that is not an attack once something is named, so the cheap checks come first.
    /// </summary>
    /// <param name="receiver">The component that tells the object of the touch.</param>
    /// <param name="other">What touches the object.</param>
    /// <param name="mark">The mark of the thrown thing.</param>
    private bool IsBodyTouchOnThrown(Component receiver, Collider2D other, out ThrownMark mark) {
        mark = null!;
        return !_isReplaying && other.gameObject.layer != (int) PhysLayers.HERO_ATTACK &&
               IsLocalHeroPart(other.gameObject) && HasMembers &&
               ThrownThings.TryGetMark(receiver.gameObject, out mark);
    }

    /// <summary>
    /// Handles the local player's body touching a thing that a creature threw. A touch that the thing answered goes to
    /// the others like a touch of an attack, with what touched it - its name, kind and place - for their twin of it to
    /// be told of a touch of the same through the same component (see <see cref="ReplayTouch"/>). The update is made
    /// after the touch, which only answers now and then while the player stands on the thing, from its name and place
    /// as they were before it.
    /// </summary>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="receiver">The component that tells the thing of the touch.</param>
    /// <param name="body">The part of the player that touches it.</param>
    /// <param name="touch">Tells the thing of the touch.</param>
    /// <param name="callback">For a receiver that hears more than one kind of touch, which it was.</param>
    private void OnLocalTouchOfThrown(
        ThrownMark mark,
        Component receiver,
        Collider2D body,
        Action touch,
        string? callback = null
    ) {
        var key = mark.Key;
        var place = ThrownThings.PlaceOf(mark);
        var turned = TurnThrownToLocalPlayer(receiver.gameObject, out var before);
        int[] dice = [];
        if (!AnswersAttack(() => dice = SharedDice.Record(touch))) {
            TurnThrownBack(turned, before);
            return;
        }

        if (key == null || !_netClient.IsConnected ||
            CreateTouchUpdate(receiver, body, ThrownThings.PathOf(key, mark, receiver.gameObject, place), callback) is
                not { } update) {
            return;
        }

        update.Hit = SharedDice.Append(update.Hit, dice);
        SendToRoomMembers(update);
        NoteTraffic(update.Scene, update.Path, $"sent a touch of the body through {update.Responder}");
    }

    /// <summary>
    /// Hook for a state machine going on from an event, which keeps a thing that a creature threw from going into a
    /// state on a replay of the partner's when that state, or one it goes on to by itself, works on the player. Such a
    /// state is about the player it touched or hurt, the partner, who has no character here: a catcher grabbed, a glob
    /// stuck to, a blob made sick this game's player, wherever they stood, for something the partner did. The thing
    /// stays as it is here, as it did before the partner's touches and hurts were sent at all.
    /// </summary>
    private bool OnThrownTransition(
        Func<Fsm, FsmTransition, bool, bool> orig,
        Fsm self,
        FsmTransition transition,
        bool isGlobal
    ) {
        try {
            if ((_isReplaying || _replayingDirection) && ThrownThings.Count > 0 && transition?.ToFsmState is { } to &&
                self.FsmComponent is { } fsm && fsm != null && ThrownThings.TryGetMark(fsm.gameObject, out _) &&
                WorksOnThePlayerFrom(to) is { } acting) {
                SayKeptOutOfState(fsm, to, acting);
                return false;
            }
        } catch (Exception e) {
            if (!_thrownFailed) {
                _thrownFailed = true;
                Logger.Error($"Could not look at where a thrown thing goes on a replay:\n{e}");
            }
        }

        return orig(self, transition!, isGlobal);
    }

    /// <summary>
    /// The first action that works on this game's player in a state, or in the states it goes on to by itself once
    /// it is finished, or null.
    /// </summary>
    private static FsmStateAction? WorksOnThePlayerFrom(FsmState state) {
        var current = state;
        for (var i = 0; current != null && i < MaxStatesLookedThrough; i++) {
            foreach (var action in current.Actions) {
                if (action != null && action.Enabled && WorksOnThePlayer(action)) {
                    return action;
                }
            }

            current = Array.Find(current.Transitions, next => next.EventName == "FINISHED")?.ToFsmState;
        }

        return null;
    }

    /// <summary>
    /// Whether an action of a thrown thing works on, or goes by, this game's player: one that acts on the character or
    /// what it carries (EntityFsmActions), one named for the player or that calls on its controller by name, a state
    /// machine run inside the state, and a message to everything that listens. A thing setting how much it hurts the
    /// player only sets something of its own.
    /// </summary>
    private static bool WorksOnThePlayer(FsmStateAction action) {
        var name = action.GetType().Name;
        if (name.StartsWith("SetDamageHero", StringComparison.Ordinal)) {
            return false;
        }

        return name.Contains("Hero") || action is RunFSM ||
               name.StartsWith("SendEventToRegister", StringComparison.Ordinal) || NamesThePlayer(action) ||
               EntityFsmActions.WorksOnThePlayerCharacter(action, false);
    }

    /// <summary>
    /// Whether a text that an action is given names the player's controller, as a call of one of its methods does.
    /// </summary>
    private static bool NamesThePlayer(FsmStateAction action) {
        var type = action.GetType();
        if (!TextFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Public | BindingFlags.Instance),
                field => field.FieldType == typeof(FsmString)
            );
            TextFields[type] = fields;
        }

        foreach (var field in fields) {
            if (field.GetValue(action) is FsmString { Value: { } text } && text.Contains("Hero")) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes down once, for each thing and state, that a replay of the partner's was kept from taking a thrown thing
    /// into a state that works on the player.
    /// </summary>
    private static void SayKeptOutOfState(PlayMakerFSM fsm, FsmState state, FsmStateAction acting) {
        var thing = ThrownThings.TryGetMark(fsm.gameObject, out var mark) ? mark.name : fsm.name;
        if (KeptOutOfStates.Add($"{thing}/{fsm.FsmName}/{state.Name}")) {
            Logger.Info(
                $"[Thrown] Kept '{thing}' ({fsm.FsmName}) out of '{state.Name}' on a replay of the partner's: " +
                $"{acting.GetType().Name} there works on this game's player"
            );
        }
    }

    /// <summary>
    /// Has a thrown thing that the local player strikes or touches go after them, as it then does in the others' games,
    /// before it answers: what it does on it that aims at a player aims at them.
    /// </summary>
    /// <param name="target">The object struck or touched.</param>
    /// <param name="before">Whom the thing went after before, for one that took no notice.</param>
    /// <returns>The mark of the thing, or null if the object is no part of a named thrown thing.</returns>
    private static ThrownMark? TurnThrownToLocalPlayer(GameObject target, out GameObject? before) {
        before = null;
        if (ThrownThings.Count == 0 || !ThrownThings.TryGetMark(target, out var mark)) {
            return null;
        }

        before = mark.Target;
        var hero = HeroController.SilentInstance;
        var player = hero != null ? hero.gameObject : null;
        if (before != player) {
            ThrownThings.TurnTo(mark, player);
        }

        return mark;
    }

    /// <summary>
    /// Has a thrown thing that the local player struck or touched without it taking any notice go after whom it went
    /// after before, as it does in the others' games, which hear nothing of it. One that went back into the pool on
    /// the way goes after no one any more.
    /// </summary>
    /// <param name="turned">The mark that <see cref="TurnThrownToLocalPlayer"/> gave, or null.</param>
    /// <param name="before">Whom it went after before.</param>
    private static void TurnThrownBack(ThrownMark? turned, GameObject? before) {
        if (turned != null && turned.Key != null && turned.Target != before) {
            ThrownThings.TurnTo(turned, before);
        }
    }

    /// <summary>
    /// Puts the twin here of a thing that a creature threw where it was in the partner's game as they struck,
    /// touched or were hurt by it, moving as it moved there, and has it go after their figure from now on, as it goes
    /// after them there.
    /// </summary>
    /// <param name="target">The object of the thing that the update is for.</param>
    /// <param name="update">The update of the partner.</param>
    private void PutThrownWhereItWas(GameObject target, CoopHitUpdate update) {
        if (!ThrownThings.IsPath(update.Path) || !ThrownThings.TryGetMark(target, out var mark)) {
            return;
        }

        ThrownThings.PutAt(mark, update.Path);
        ThrownThings.TurnTo(mark, FigureOf(update.PlayerId));
    }

    /// <summary>
    /// The scene and path that find an object in the others' games: its place in the room, or for a part of a thing
    /// that a creature threw its name and where it is (see <see cref="ThrownThings.PathOf(GameObject)"/>), with the
    /// room it is in.
    /// </summary>
    /// <param name="target">The object.</param>
    /// <param name="scene">The scene.</param>
    /// <param name="path">The path.</param>
    /// <returns>Whether the object can be found by the others.</returns>
    private static bool TryGetPlace(GameObject target, out string scene, out string path) {
        if (ThrownThings.Count > 0 && ThrownThings.PathOf(target) is { } thrownPath) {
            scene = SceneManager.GetActiveScene().name;
            path = thrownPath;
            return true;
        }

        scene = target.scene.name;
        path = "";
        if (!target.scene.IsValid() || scene == "DontDestroyOnLoad") {
            return false;
        }

        path = ScenePath.Get(target.transform);
        return true;
    }

    /// <summary>
    /// Finds the object that an update of the partner names (see <see cref="TryGetPlace"/>).
    /// </summary>
    private static GameObject? FindPlace(CoopHitUpdate update) {
        return ThrownThings.IsPath(update.Path)
            ? ThrownThings.Find(update.Path)
            : ScenePath.Find(update.Path, update.Scene);
    }

    /// <summary>
    /// What a throw of a creature that this game runs says of the player it goes after (see
    /// <see cref="ThrownThings.CodeOf"/>).
    /// </summary>
    /// <param name="target">The player's object: this game's player or the figure of another.</param>
    private ushort CodeOfPlayer(GameObject? target) {
        if (target == null) {
            return ThrownThings.NoPlayer;
        }

        var hero = HeroController.SilentInstance;
        if (hero != null && target == hero.gameObject) {
            return ThrownThings.ThrowersPlayer;
        }

        foreach (var pair in _playerData) {
            if (pair.Value.PlayerObject == target) {
                return pair.Key;
            }
        }

        return ThrownThings.NoPlayer;
    }

    /// <summary>
    /// The player here that a throw from another game says a thing goes after (see
    /// <see cref="ThrownThings.FigureOf"/>): the figure of the player of that game, the figure of another, or this
    /// game's player, whose number is not among those of the others.
    /// </summary>
    /// <param name="code">What the throw says.</param>
    /// <param name="thrower">The player of the game that threw it.</param>
    private GameObject? FigureOfThrowTarget(ushort code, ushort thrower) {
        switch (code) {
            case ThrownThings.NoPlayer:
                return null;
            case ThrownThings.ThrowersPlayer:
                return FigureOf(thrower);
        }

        if (_playerData.ContainsKey(code)) {
            return FigureOf(code);
        }

        var hero = HeroController.SilentInstance;
        return hero != null ? hero.gameObject : null;
    }
}
