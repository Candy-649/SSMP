using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
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
/// moved as it happened, which its twin is put at first, and the thing goes after that player from then on.
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// Binding flags for the static methods that are hooked.
    /// </summary>
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The thrown thing whose state machines are watched while the local player touches it or is hurt by it, or null.
    /// </summary>
    private ThrownMark? _watchedThrown;

    /// <summary>
    /// How many changes of state are under way while a thrown thing is watched. Only what is told from outside is
    /// written down: what the state machines then tell each other and themselves on the way follows from it again in
    /// the other games.
    /// </summary>
    private int _watchedDepth;

    /// <summary>
    /// What the state machines of the watched thrown thing were told and went on from, in order.
    /// </summary>
    private readonly List<(PlayMakerFSM Fsm, string EventName)> _watchedEvents = [];

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
    /// Registers the hooks that name thrown things, roll their bounces and watch what the local player's body does to
    /// them.
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
    private void DeregisterThrownHooks() {
        ThrownThings.CodeOf = null;
        ThrownThings.FigureOf = null;
        _watchedThrown = null;
        _watchedDepth = 0;
        _watchedEvents.Clear();
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

        var action = FsmExecutionStack.ExecutingAction;
        if (action is not (SpawnObjectFromGlobalPool or FlingObjectsFromGlobalPool or FlingObjectsFromGlobalPoolVel) ||
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
    /// Hook for what a thing that hurt the local player tells of it, which for a thrown thing is watched and sent to
    /// the others (see <see cref="WatchThrown"/>): a caltrop that hurt the player flies off, in every game.
    /// </summary>
    private void OnSendHeroDamagedEvent(Action<DamageHero> orig, DamageHero self) {
        if (ThrownThings.Count == 0 || _isReplaying || _watchedThrown != null || !HasMembers ||
            !ThrownThings.TryGetMark(self.gameObject, out var mark)) {
            orig(self);
            return;
        }

        WatchThrown(mark, () => orig(self), "a hurt");
    }

    /// <summary>
    /// Whether something touching an object is the body of the local player, rather than their attack, and the object
    /// a part of a named thrown thing, which then hears of it through <see cref="WatchThrown"/>. Asked of every touch
    /// that is not an attack once something is named, so the cheap checks come first.
    /// </summary>
    /// <param name="receiver">The component that tells the object of the touch.</param>
    /// <param name="other">What touches the object.</param>
    /// <param name="mark">The mark of the thrown thing.</param>
    private bool IsBodyTouchOnThrown(Component receiver, Collider2D other, out ThrownMark mark) {
        mark = null!;
        return !_isReplaying && _watchedThrown == null && other.gameObject.layer != (int) PhysLayers.HERO_ATTACK &&
               IsLocalHeroPart(other.gameObject) && HasMembers &&
               ThrownThings.TryGetMark(receiver.gameObject, out mark);
    }

    /// <summary>
    /// Has the local player touch, or be hurt by, a thrown thing, and sends the others what its state machines were
    /// told of it and went on from, for their twin of it to be told the same. A touch of the body and a hurt are this
    /// game's alone: the figure of this player in another game does not touch or get hurt by anything there.
    /// </summary>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="act">What touches it or tells it of the hurt.</param>
    /// <param name="what">What it is, for the log.</param>
    private void WatchThrown(ThrownMark mark, Action act, string what) {
        // Its name and where it was as it happened, which it is put at in the others' games first: what it is told can
        // put it back into the pool, which takes its name off it
        var key = mark.Key;
        var place = ThrownThings.PlaceOf(mark);
        var turned = TurnThrownToLocalPlayer(mark.gameObject, out var before);

        _watchedThrown = mark;
        _watchedDepth = 0;
        _watchedEvents.Clear();
        try {
            act();
        } finally {
            _watchedThrown = null;
        }

        try {
            if (_watchedEvents.Count == 0) {
                // It took no notice, and goes after whom it went after, as it does in the others' games
                TurnThrownBack(turned, before);
                return;
            }

            if (key != null && _netClient.IsConnected) {
                SendWatchedEvents(key, mark, place, what);
            }
        } catch (Exception e) {
            if (!_thrownFailed) {
                _thrownFailed = true;
                Logger.Error($"Could not send what a thrown thing was told:\n{e}");
            }
        } finally {
            _watchedEvents.Clear();
        }
    }

    /// <summary>
    /// Sends what the state machines of the watched thrown thing were told, one update for each state machine, under
    /// the name and place of the thing as it was (see <see cref="WatchThrown"/>).
    /// </summary>
    /// <param name="key">The key of the thing as it was.</param>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="place">Where the thing was.</param>
    /// <param name="what">What it was, for the log.</param>
    private void SendWatchedEvents(string key, ThrownMark mark, ThrownThings.ThrownPlace place, string what) {
        var told = new List<PlayMakerFSM>();
        var events = new Dictionary<PlayMakerFSM, List<(string EventName, int[] Dice)>>();
        foreach (var (fsm, eventName) in _watchedEvents) {
            if (fsm == null) {
                continue;
            }

            if (!events.TryGetValue(fsm, out var list)) {
                list = [];
                events[fsm] = list;
                told.Add(fsm);
            }

            // Without dice: what a named thing rolls goes by its name
            list.Add((eventName, []));
        }

        foreach (var fsm in told) {
            var path = ThrownThings.PathOf(key, mark, fsm.gameObject, place);
            if (CreateEventsUpdate(fsm, events[fsm], path) is not { } update) {
                continue;
            }

            SendToRoomMembers(update);
            NoteTraffic(
                update.Scene, update.Path, $"sent {events[fsm].Count} event(s) of {what} to {update.Responder}"
            );
        }
    }

    /// <summary>
    /// Hook for a state machine going on from an event, which writes down the events that the watched thrown thing is
    /// told from outside (see <see cref="WatchThrown"/>). A state machine finishing a state is not told anything.
    /// </summary>
    private bool OnThrownTransition(
        Func<Fsm, FsmTransition, bool, bool> orig,
        Fsm self,
        FsmTransition transition,
        bool isGlobal
    ) {
        if (_watchedThrown is not { } watched || _watchedDepth > 0) {
            return orig(self, transition, isGlobal);
        }

        var index = -1;
        if (transition?.EventName is { Length: > 0 } eventName && eventName != "FINISHED" &&
            self.FsmComponent is { } fsm && fsm != null && watched != null &&
            fsm.transform.IsChildOf(watched.transform)) {
            index = _watchedEvents.Count;
            _watchedEvents.Add((fsm, eventName));
        }

        var changed = orig(self, transition!, isGlobal);
        if (!changed && index >= 0 && index < _watchedEvents.Count) {
            _watchedEvents.RemoveAt(index);
        }

        return changed;
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
    /// Puts the twin here of a thing that a creature threw where it was in the partner's game as they struck or
    /// touched it, moving as it moved there, and has it go after their figure from now on, as it goes after them
    /// there.
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
