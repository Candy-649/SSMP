using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Bounds = UnityEngine.Bounds;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// The zones by which an NPC shows itself, hides, wakes, sleeps or waves as a player comes near or goes away. They lie
/// on a layer that meets the player of their game and never the partner's figure, so each game showed such an NPC by
/// where its own player was: one that pops up beside whoever comes near, at one of a few places in its room, popped up
/// beside each player in their own game only, and the two games showed it in different places (USER 10-23). Such a
/// zone now counts every player, like the alert ranges that are answered for either player: a player coming in is a
/// player coming in, and the zone is left when the last player in it goes. A zone whose event takes the player of its
/// game into a scene of their own, holding them, turning them, roaring at them or talking to them unasked, or writes
/// their save, stays the local player's: each player has such a scene for themselves, when they walk in.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How many states after the one that a zone's event leads to are looked through for one that is personal to the
    /// player of the game. A first meeting can take them two states after the one that asks whether it is the first.
    /// </summary>
    private const int NpcZoneReachStates = 3;

    /// <summary>
    /// The kinds of action with which a state takes the player of its game into a scene of their own or writes their
    /// save, an item or a collectable they get among it, besides those whose names begin with SetPlayerData.
    /// </summary>
    private static readonly HashSet<string> PersonalActionNames = [
        nameof(RunFSM), nameof(RunDialogue), nameof(HeroTurnToFace), "HeroLockState", "HeroControllerMethods",
        "AddHeroInputBlocker", nameof(StartRoarEmitter), "PlayerDataIntAdd", "IncrementPlayerDataInt",
        "DecrementPlayerDataInt", "SavedItemGet", "SavedItemGetV2", "SavedItemGetDelayed", "CollectableItemCollect",
        "CollectableItemTake", "CollectableItemTakeV2", "GetQuestReward", "GetQuestRewardV2", "EndQuest",
        "SetToolUnlocked", "UnlockCrest", "AutoEquipCrestV4"
    ];

    /// <summary>
    /// The methods of the player character that, sent as a message, take its control or give it back.
    /// </summary>
    private static readonly HashSet<string> PlayerControlMessages = [
        "RelinquishControl", "RelinquishControlNotVelocity", "RegainControl", "StopAnimationControl",
        "StartAnimationControl", "IgnoreInput", "AcceptInput"
    ];

    /// <summary>
    /// The fields of each kind of action that name an event, which the action sends to its own state machine.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> SentEventFields = new();

    /// <summary>
    /// The zones of NPCs that count every player, by the action that listens to one while its state is the current one.
    /// </summary>
    private static readonly Dictionary<TriggerEnterEventSubscribe, NpcZone> NpcZones = new();

    /// <summary>
    /// The zones of NPCs that a state of the room listened to before.
    /// </summary>
    private static readonly HashSet<TriggerEnterEvent> NpcZonesHeard = [];

    /// <summary>
    /// Whether a count of the players in a zone, asked by an NPC, counts every player, by the action that asks it.
    /// </summary>
    private static readonly Dictionary<CheckTrackTriggerCount, bool> NpcCountsEveryPlayer = new();

    /// <summary>
    /// The other players in each zone that an NPC counts the players in, worked out once a frame.
    /// </summary>
    private static readonly Dictionary<TrackTriggerObjects, NpcZoneCount> NpcZonePartnerCounts = new();

    /// <summary>
    /// The zones that were written down as counting every player, so that each is written once.
    /// </summary>
    private static readonly HashSet<string> NpcZonesNoted = [];

    /// <summary>
    /// The zones that an update goes through, kept so that sending an event, which can end the listening, does not
    /// change what is gone through.
    /// </summary>
    private static readonly List<NpcZone> NpcZonesToUpdate = [];

    /// <summary>
    /// The players in a zone that went out of it since the last update.
    /// </summary>
    private static readonly List<GameObject> NpcZoneLeavers = [];

    /// <summary>
    /// The players in the room as an update of the zones began. Kept apart from the list that
    /// <see cref="PlayerTargetRegistry.GetTrackedPlayers"/> fills anew at every call, which an event sent to a state
    /// machine can lead to.
    /// </summary>
    private static readonly List<GameObject> NpcZonePlayers = [];

    /// <summary>
    /// Where the body of each other player in the room was as this game first saw them there.
    /// </summary>
    private static readonly Dictionary<GameObject, Bounds> NpcZoneArrivals = new();

    /// <summary>
    /// The other players who are no longer in the room, whose arrival is forgotten.
    /// </summary>
    private static readonly List<GameObject> NpcZonePlayersGone = [];

    /// <summary>
    /// Hook for <see cref="TriggerEnterEventSubscribe"/>.OnEnter, which starts listening to a zone.
    /// </summary>
    private Hook? _triggerEnterSubscribeEnterHook;

    /// <summary>
    /// Hook for <see cref="TriggerEnterEventSubscribe"/>.OnExit, which stops listening to it.
    /// </summary>
    private Hook? _triggerEnterSubscribeExitHook;

    /// <summary>
    /// Hook for <see cref="TriggerEnterEventSubscribe"/>.SendExitedEvent, by which the local player leaves a zone.
    /// </summary>
    private Hook? _triggerEnterSubscribeExitedHook;

    /// <summary>
    /// Hook for <see cref="CheckTrackTriggerCount"/>.CheckCount, which counts the players in a zone.
    /// </summary>
    private Hook? _checkTrackTriggerCountHook;

    /// <summary>
    /// Registers the hooks with which the zones of NPCs count every player.
    /// </summary>
    private void RegisterNpcZoneHooks() {
        _triggerEnterSubscribeEnterHook = TryCreateHook(
            typeof(TriggerEnterEventSubscribe), "OnEnter", OnTriggerEnterSubscribeEnter
        );
        _triggerEnterSubscribeExitHook = TryCreateHook(
            typeof(TriggerEnterEventSubscribe), "OnExit", OnTriggerEnterSubscribeExit
        );
        _triggerEnterSubscribeExitedHook = TryCreateHook(
            typeof(TriggerEnterEventSubscribe), "SendExitedEvent", OnTriggerEnterSubscribeExited
        );
        _checkTrackTriggerCountHook = TryCreateHook(
            typeof(CheckTrackTriggerCount), "CheckCount", OnCheckTrackTriggerCount
        );

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnNpcZonesUpdate;
        SceneManager.activeSceneChanged += OnNpcZonesSceneChanged;
    }

    /// <summary>
    /// Disposes the hooks with which the zones of NPCs count every player.
    /// </summary>
    private void DisposeNpcZoneHooks() {
        _triggerEnterSubscribeEnterHook?.Dispose();
        _triggerEnterSubscribeEnterHook = null;

        _triggerEnterSubscribeExitHook?.Dispose();
        _triggerEnterSubscribeExitHook = null;

        _triggerEnterSubscribeExitedHook?.Dispose();
        _triggerEnterSubscribeExitedHook = null;

        _checkTrackTriggerCountHook?.Dispose();
        _checkTrackTriggerCountHook = null;

        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnNpcZonesUpdate;
        SceneManager.activeSceneChanged -= OnNpcZonesSceneChanged;

        NpcZones.Clear();
        NpcZonesHeard.Clear();
        NpcCountsEveryPlayer.Clear();
        NpcZonePartnerCounts.Clear();
        NpcZonesNoted.Clear();
        NpcZoneArrivals.Clear();
    }

    /// <summary>
    /// Forgets what was worked out in the room that was left, and where the other players arrived in it. The room that
    /// is entered starts its state machines before this, so the zones that they listen to are kept: those of the room
    /// that was left go with its objects.
    /// </summary>
    private static void OnNpcZonesSceneChanged(Scene oldScene, Scene newScene) {
        NpcZonesHeard.RemoveWhere(trigger => trigger == null);
        NpcCountsEveryPlayer.Clear();
        NpcZonePartnerCounts.Clear();
        NpcZoneArrivals.Clear();
    }

    /// <summary>
    /// Starts counting every player in the zone that an NPC's state listens to, if it is a zone of an NPC that the
    /// partner's figure does not meet and its events lead to nothing personal to the player of this game.
    /// </summary>
    private static void OnTriggerEnterSubscribeEnter(
        Action<TriggerEnterEventSubscribe> orig,
        TriggerEnterEventSubscribe self
    ) {
        orig(self);

        if (self.trigger is not { IsNone: false } || self.trigger.Value is not TriggerEnterEvent trigger ||
            trigger == null || self.Fsm is not { } fsm || self.State is not { } state || !IsNpc(fsm.GameObject) ||
            !MeetsTheLocalPlayerAlone(trigger.gameObject)) {
            return;
        }

        foreach (var sent in new[] { self.triggerEnteredEvent, self.triggerExitedEvent, self.triggerStayedEvent }) {
            if (sent != null && !string.IsNullOrEmpty(sent.Name) && LeadsToSomethingPersonal(fsm, state, sent.Name)) {
                return;
            }
        }

        var zone = new NpcZone(self, trigger, trigger.GetComponents<Collider2D>());

        // The game hears its player come into a zone only while a state listens to it: one who is already in it as a
        // state begins to listen wakes nothing until they come in again. So does another player who came in while this
        // game watched. One who was in the zone as this game first saw them in the room came in before it could see,
        // and most likely woke the NPC in their own game: as the zone is listened to for the first time in the room,
        // they are coming in.
        var firstListen = NpcZonesHeard.Add(trigger);
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player == null || IsLocalHero(player) || !IsInZone(zone.Colliders, player)) {
                continue;
            }

            if (!firstListen ||
                (NpcZoneArrivals.TryGetValue(player, out var arrival) && !IsInZone(zone.Colliders, arrival))) {
                zone.PartnersInside.Add(player);
            }
        }

        NpcZones[self] = zone;
        NoteNpcZone(fsm, trigger.name);
    }

    /// <summary>
    /// Stops counting the players in a zone as its state stops listening to it.
    /// </summary>
    private static void OnTriggerEnterSubscribeExit(
        Action<TriggerEnterEventSubscribe> orig,
        TriggerEnterEventSubscribe self
    ) {
        NpcZones.Remove(self);
        orig(self);
    }

    /// <summary>
    /// Keeps the local player's leaving a zone from being heard while another player is still in it.
    /// </summary>
    private static void OnTriggerEnterSubscribeExited(
        Action<TriggerEnterEventSubscribe, Collider2D, GameObject> orig,
        TriggerEnterEventSubscribe self,
        Collider2D collider,
        GameObject sender
    ) {
        if (NpcZones.TryGetValue(self, out var zone) && zone.PartnersInside.Count > 0) {
            return;
        }

        orig(self, collider, sender);
    }

    /// <summary>
    /// Tells the state that listens to a zone of the other players who came into it, who stay in it and who left it,
    /// the last of them only once nobody is in it any more.
    /// </summary>
    private static void OnNpcZonesUpdate() {
        if (NpcZones.Count == 0 && NpcZonesHeard.Count == 0) {
            return;
        }

        if (global::GameManager.instance is { } gameManager && gameManager.IsInSceneTransition) {
            return;
        }

        NpcZonePlayers.Clear();
        NpcZonePlayers.AddRange(PlayerTargetRegistry.GetTrackedPlayers());
        var players = NpcZonePlayers;
        NoteNpcZoneArrivals(players);

        NpcZonesToUpdate.Clear();
        NpcZonesToUpdate.AddRange(NpcZones.Values);
        foreach (var zone in NpcZonesToUpdate) {
            // Sending an event can take the state machine out of the listening state, which ends the zone's turn, or
            // out and back into it, which begins another
            if (!IsListenedTo(zone)) {
                continue;
            }

            var fsm = zone.Action.Fsm;
            if (zone.Trigger == null || fsm?.ActiveState != zone.Action.State) {
                NpcZones.Remove(zone.Action);
                continue;
            }

            if (!zone.Trigger.active) {
                continue;
            }

            NpcZoneLeavers.Clear();
            foreach (var partner in zone.PartnersInside) {
                if (partner == null || !players.Contains(partner) || !IsInZone(zone.Colliders, partner)) {
                    NpcZoneLeavers.Add(partner!);
                }
            }

            foreach (var player in players) {
                if (player == null || IsLocalHero(player) || zone.PartnersInside.Contains(player) ||
                    !IsInZone(zone.Colliders, player)) {
                    continue;
                }

                zone.PartnersInside.Add(player);
                if (zone.Action.triggerEnteredEvent is { } entered && !string.IsNullOrEmpty(entered.Name)) {
                    fsm.Event(entered);
                    if (!IsListenedTo(zone)) {
                        break;
                    }
                }
            }

            if (!IsListenedTo(zone)) {
                continue;
            }

            foreach (var leaver in NpcZoneLeavers) {
                zone.PartnersInside.Remove(leaver);
            }

            if (NpcZoneLeavers.Count > 0 && zone.PartnersInside.Count == 0 &&
                zone.Action.triggerExitedEvent is { } exited && !string.IsNullOrEmpty(exited.Name) &&
                !IsLocalHeroInZone(zone.Colliders)) {
                fsm.Event(exited);
                continue;
            }

            // The game says so at every physics step while somebody is in it
            if (zone.PartnersInside.Count > 0 && zone.Action.triggerStayedEvent is { } stayed &&
                !string.IsNullOrEmpty(stayed.Name)) {
                fsm.Event(stayed);
            }
        }
    }

    /// <summary>
    /// Whether the state that listens to a zone still does so in this turn of listening.
    /// </summary>
    private static bool IsListenedTo(NpcZone zone) =>
        NpcZones.TryGetValue(zone.Action, out var current) && current == zone;

    /// <summary>
    /// Writes down where each other player in the room is as this game first sees them there, and forgets those who
    /// left it.
    /// </summary>
    private static void NoteNpcZoneArrivals(List<GameObject> players) {
        NpcZonePlayersGone.Clear();
        foreach (var player in NpcZoneArrivals.Keys) {
            if (player == null || !players.Contains(player)) {
                NpcZonePlayersGone.Add(player!);
            }
        }

        foreach (var player in NpcZonePlayersGone) {
            NpcZoneArrivals.Remove(player);
        }

        foreach (var player in players) {
            if (player != null && !IsLocalHero(player) && !NpcZoneArrivals.ContainsKey(player) &&
                GetBody(player) is { } body) {
                NpcZoneArrivals[player] = body.bounds;
            }
        }
    }

    /// <summary>
    /// Counts the other players too in a zone that an NPC counts the players in, if the partner's figure does not
    /// meet it, what the count sets off is nothing personal to the player of this game, and it asks whether anybody
    /// is in the zone or nobody.
    /// </summary>
    private static bool OnCheckTrackTriggerCount(Func<CheckTrackTriggerCount, bool> orig, CheckTrackTriggerCount self) {
        if (self.track is not { } track || track == null || !CountsEveryPlayer(self) ||
            !AsksAnybodyOrNobody((Extensions.IntTest) self.test.Value, self.count.Value)) {
            return orig(self);
        }

        if (!NpcZonePartnerCounts.TryGetValue(track, out var counted)) {
            counted = new NpcZoneCount(track.GetComponents<Collider2D>());
            NpcZonePartnerCounts[track] = counted;
        }

        var frame = Time.frameCount;
        if (counted.Frame != frame) {
            counted.Frame = frame;
            counted.Count = 0;
            foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
                if (player != null && !IsLocalHero(player) && IsInZone(counted.Colliders, player)) {
                    counted.Count++;
                }
            }
        }

        return (track.InsideCount + counted.Count).Test((Extensions.IntTest) self.test.Value, self.count.Value);
    }

    /// <summary>
    /// Whether a test of how many players are in a zone asks whether anybody is in it, or nobody. Other tests, such
    /// as whether exactly one is, are left to the game: the zones of a room's music ask that of the player of the game.
    /// </summary>
    private static bool AsksAnybodyOrNobody(Extensions.IntTest test, int count) => test switch {
        Extensions.IntTest.Equal or Extensions.IntTest.LessThanOrEqual or Extensions.IntTest.MoreThan => count == 0,
        Extensions.IntTest.LessThan or Extensions.IntTest.MoreThanOrEqual => count == 1,
        _ => false
    };

    /// <summary>
    /// Whether a count of the players in a zone, asked by an action, counts every player: it is asked by an NPC, about
    /// a zone that the partner's figure does not meet, and the event it sends leads to nothing personal to the player
    /// of this game.
    /// </summary>
    private static bool CountsEveryPlayer(CheckTrackTriggerCount action) {
        if (NpcCountsEveryPlayer.TryGetValue(action, out var known)) {
            return known;
        }

        var counts = action.Fsm is { } fsm && action.State is { } state && IsNpc(fsm.GameObject) &&
                     MeetsTheLocalPlayerAlone(action.track.gameObject) &&
                     action.successEvent is { } sent && !string.IsNullOrEmpty(sent.Name) &&
                     !LeadsToSomethingPersonal(fsm, state, sent.Name);
        NpcCountsEveryPlayer[action] = counts;
        if (counts) {
            NoteNpcZone(action.Fsm!, action.track.name);
        }

        return counts;
    }

    /// <summary>
    /// Writes down once that a zone of an NPC counts every player.
    /// </summary>
    private static void NoteNpcZone(HutongGames.PlayMaker.Fsm fsm, string zoneName) {
        var key = $"{fsm.GameObject?.scene.name}/{fsm.GameObject?.name}/{fsm.Name}/{zoneName}";
        if (NpcZonesNoted.Add(key)) {
            Logger.Info(
                $"[NpcZone] The zone '{zoneName}' of '{fsm.GameObject?.name}' ('{fsm.Name}') counts every player"
            );
        }
    }

    /// <summary>
    /// Whether an object belongs to an NPC: it is called one, or it has a component of one.
    /// </summary>
    private static bool IsNpc(GameObject? gameObject) {
        if (gameObject == null) {
            return false;
        }

        if (gameObject.name.Contains("NPC")) {
            return true;
        }

        foreach (var component in gameObject.GetComponents<Component>()) {
            if (component != null && component.GetType().Name.Contains("NPC")) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a zone meets the player of its game and not the partner's figure, by the layers that meet each other.
    /// </summary>
    private static bool MeetsTheLocalPlayerAlone(GameObject zone) {
        if (HeroController.SilentInstance is not { } hero) {
            return false;
        }

        var figure = PlayerTargetRegistry.GetTrackedPlayers().Find(player => player != null && !IsLocalHero(player));
        var figureLayer = figure != null ? figure.layer : 0;
        return !Physics2D.GetIgnoreLayerCollision(zone.layer, hero.gameObject.layer) &&
               Physics2D.GetIgnoreLayerCollision(zone.layer, figureLayer);
    }

    /// <summary>
    /// Whether the event that a state sends itself takes the state machine, within a few states, into one with
    /// something personal to the player of the game. Only the events that the states on the way send themselves are
    /// followed, not those of something the player does, like talking to the NPC.
    /// </summary>
    private static bool LeadsToSomethingPersonal(HutongGames.PlayMaker.Fsm fsm, FsmState state, string eventName) {
        return EntityFsmActions.FindTransition(fsm, state, eventName) is { } next &&
               IsPersonal(fsm, next, NpcZoneReachStates);
    }

    /// <summary>
    /// Whether a state, or one that it goes on to by itself within the given number of states, takes the player of
    /// the game into a scene of their own or writes their save.
    /// </summary>
    private static bool IsPersonal(HutongGames.PlayMaker.Fsm fsm, FsmState state, int statesOn) {
        foreach (var action in state.Actions) {
            if (action is { Enabled: true } && IsPersonal(action)) {
                return true;
            }
        }

        if (statesOn == 0) {
            return false;
        }

        foreach (var transition in state.Transitions) {
            if (transition.EventName != "FINISHED" && !SendsItself(state, transition.EventName)) {
                continue;
            }

            if (transition.ToFsmState is { } next && next != state && IsPersonal(fsm, next, statesOn - 1)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an action takes the player of the game or writes their save: one of the kinds that do, a call to the
    /// player character, or a message that takes its control or gives it back.
    /// </summary>
    private static bool IsPersonal(FsmStateAction action) {
        switch (action) {
            case CallMethodProper callMethod:
                return callMethod.behaviour is { IsNone: false } behaviour &&
                       behaviour.Value == nameof(HeroController);
            case SendMessage message:
                return message.functionCall is { } function && PlayerControlMessages.Contains(function.FunctionName);
            case SendMessageV2 messageV2:
                return messageV2.functionCall is { } functionV2 &&
                       PlayerControlMessages.Contains(functionV2.FunctionName);
            default:
                var name = action.GetType().Name;
                return PersonalActionNames.Contains(name) || name.StartsWith("SetPlayerData", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Whether one of the actions of a state names the event, which it sends to its own state machine.
    /// </summary>
    private static bool SendsItself(FsmState state, string eventName) {
        foreach (var action in state.Actions) {
            if (action is not { Enabled: true }) {
                continue;
            }

            foreach (var field in GetSentEventFields(action.GetType())) {
                switch (field.GetValue(action)) {
                    case FsmEvent sent when sent.Name == eventName:
                        return true;
                    case FsmEvent[] events:
                        foreach (var sentOne in events) {
                            if (sentOne != null && sentOne.Name == eventName) {
                                return true;
                            }
                        }

                        break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The fields of a kind of action that name events.
    /// </summary>
    private static FieldInfo[] GetSentEventFields(Type type) {
        if (!SentEventFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                field => field.FieldType == typeof(FsmEvent) || field.FieldType == typeof(FsmEvent[])
            );
            SentEventFields[type] = fields;
        }

        return fields;
    }

    /// <summary>
    /// Whether an object is the player of this game.
    /// </summary>
    private static bool IsLocalHero(GameObject player) =>
        HeroController.SilentInstance is { } hero && hero.gameObject == player;

    /// <summary>
    /// Whether the player of this game is in a zone.
    /// </summary>
    private static bool IsLocalHeroInZone(Collider2D[] zone) =>
        HeroController.SilentInstance is { } hero && IsInZone(zone, hero.gameObject);

    /// <summary>
    /// The collider of a player's body, if they are in the room with it on.
    /// </summary>
    private static Collider2D? GetBody(GameObject player) =>
        player.activeInHierarchy && player.GetComponent<Collider2D>() is { enabled: true } body ? body : null;

    /// <summary>
    /// Whether the body of a player is in a zone.
    /// </summary>
    private static bool IsInZone(Collider2D[] zone, GameObject player) =>
        GetBody(player) is { } body && IsInZone(zone, body.bounds);

    /// <summary>
    /// Whether the bounds of a body meet those of a zone.
    /// </summary>
    private static bool IsInZone(Collider2D[] zone, Bounds body) {
        foreach (var collider in zone) {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) {
                continue;
            }

            var bounds = collider.bounds;
            if (bounds.min.x <= body.max.x && bounds.max.x >= body.min.x &&
                bounds.min.y <= body.max.y && bounds.max.y >= body.min.y) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A zone of an NPC that counts every player, while the state that listens to it is the current one.
    /// </summary>
    /// <param name="action">The action that listens to the zone.</param>
    /// <param name="trigger">The zone.</param>
    /// <param name="colliders">The colliders of the zone.</param>
    private sealed class NpcZone(TriggerEnterEventSubscribe action, TriggerEnterEvent trigger, Collider2D[] colliders) {
        /// <summary>
        /// The action that listens to the zone.
        /// </summary>
        public TriggerEnterEventSubscribe Action { get; } = action;

        /// <summary>
        /// The zone.
        /// </summary>
        public TriggerEnterEvent Trigger { get; } = trigger;

        /// <summary>
        /// The colliders of the zone.
        /// </summary>
        public Collider2D[] Colliders { get; } = colliders;

        /// <summary>
        /// The other players in the zone.
        /// </summary>
        public HashSet<GameObject> PartnersInside { get; } = [];
    }

    /// <summary>
    /// How many other players are in a zone that an NPC counts the players in, as of a frame.
    /// </summary>
    /// <param name="colliders">The colliders of the zone.</param>
    private sealed class NpcZoneCount(Collider2D[] colliders) {
        /// <summary>
        /// The colliders of the zone.
        /// </summary>
        public Collider2D[] Colliders { get; } = colliders;

        /// <summary>
        /// The frame the count is of, or -1 for none yet.
        /// </summary>
        public int Frame { get; set; } = -1;

        /// <summary>
        /// How many other players are in the zone.
        /// </summary>
        public int Count { get; set; }
    }
}
