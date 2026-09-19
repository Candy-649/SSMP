using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;
using SSMP.Util;

/// <summary>
/// Lifts that an FSM runs in a checked two-player save (see CoopSave.Lifts). A platform lift starts a ride when the hero
/// lands on it, and comes by itself when the hero is at the other end, both through an event of its FSM into its bob
/// state. With two players it only comes by itself for a player close to its shaft, and not for a player who stood at
/// its stop while it left without them, until they left that stop and came back. The one-time story lift only starts
/// once both players are in it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the FSM of platform lifts.
    /// </summary>
    private const string LiftFsmName = "Lift Control";

    /// <summary>
    /// The state of a platform lift in which a ride starts.
    /// </summary>
    private const string LiftBobState = "Bob";

    /// <summary>
    /// The state of a platform lift after its bob, which moves it the way that its "Is Down" variable says.
    /// </summary>
    private const string LiftDirectionState = "Up or Down?";

    /// <summary>
    /// How much of the bob of a platform lift can be skipped, in seconds, before a replay starts after it.
    /// </summary>
    private const float LiftBobSkip = 0.25f;

    /// <summary>
    /// How far sideways from its shaft a player may be and still wait for a platform lift.
    /// </summary>
    private const float LiftShaftReach = 10f;

    /// <summary>
    /// The name of the FSM of the story lift.
    /// </summary>
    private const string StoryLiftFsmName = "Control";

    /// <summary>
    /// The state of the story lift that waits for the hero to enter, and the state that starts it.
    /// </summary>
    private const string StoryLiftIdleState = "Idle", StoryLiftEnteredState = "Entered";

    /// <summary>
    /// The states of platform lifts that stand still and start a ride on an event into their bob state, with the stop
    /// they stand at: 0 at the bottom and 1 at the top.
    /// </summary>
    private static readonly Dictionary<string, int> LiftStandingStates = new() {
        ["Up"] = 1, ["Up 2"] = 1, ["Up Inactive"] = 1, ["Down"] = 0, ["Down 2"] = 0, ["Down Inactive"] = 0
    };

    /// <summary>
    /// The states of platform lifts on a ride, with the stop they go to, or -1 for the way that "Is Down" says.
    /// </summary>
    private static readonly Dictionary<string, int> LiftRideStates = new() {
        [LiftBobState] = -1, [LiftDirectionState] = -1, ["Flip Dir"] = -1, ["Move Up"] = 1, ["Reached Top"] = 1,
        ["Move Down"] = 0, ["Reached Bot"] = 0, ["Hero Stop"] = 0, ["Wait For Hero Safe"] = 0,
        ["Ensure Chain Spline"] = 0
    };

    /// <summary>
    /// The states of the story lift from its start until its fall moves the heroes.
    /// </summary>
    private static readonly HashSet<string> StoryLiftRideStates = [
        StoryLiftEnteredState, "Ready 1", "Rise Pause", "Rise", "Rise Stop", "Drop 1", "Drop Stop 1", "Ready 2",
        "Ready 3", "Drop Pause", "Drop 2", "Drop 3", "Drop Stop 2", "Break Wait 1", "Break Wait 2", "Break Wait 3",
        "Break Pause", "Break Shake", "Break Down"
    ];

    /// <summary>
    /// The FSMs that the sync looked at, with their lift or null for FSMs that aren't lifts.
    /// </summary>
    private readonly Dictionary<Fsm, SyncedLift?> _liftFsms = new();

    /// <summary>
    /// A platform lift that an FSM moves between the bottom and the top.
    /// </summary>
    private class FsmLift : SyncedLift {
        /// <summary>
        /// The FSM of the lift.
        /// </summary>
        public required Fsm Fsm { get; init; }

        /// <summary>
        /// Whether the lift stands at the bottom, which makes its next ride go up.
        /// </summary>
        public required FsmBool IsDown { get; init; }

        /// <summary>
        /// The height of the hero above which the lift comes up, or null if the FSM has none.
        /// </summary>
        public FsmFloat? MidHeight { get; init; }

        /// <summary>
        /// The collider that the hero stands on.
        /// </summary>
        public Collider2D? Platform { get; init; }


        /// <summary>
        /// The stop that the lift stood at when the sync last looked, or -1 while it moves.
        /// </summary>
        public int StoodAt { get; set; } = -1;

        /// <summary>
        /// The stop that the lift left while the local hero stood there without riding it, or -1. Standing there doesn't
        /// call it back until the hero left that stop.
        /// </summary>
        public int DeclinedStop { get; set; } = -1;

        /// <inheritdoc />
        public override MonoBehaviour Owner => Fsm.Owner;

        /// <inheritdoc />
        public override string FsmName => Fsm.Name;

        /// <inheritdoc />
        public override bool IsMoving => Fsm.ActiveStateName is { } state && LiftRideStates.ContainsKey(state);

        /// <summary>
        /// Whether the lift stands still in a state from which a ride starts.
        /// </summary>
        public bool IsStanding => Fsm.ActiveStateName is { } state && LiftStandingStates.ContainsKey(state);

        /// <inheritdoc />
        public override int Stop {
            get {
                var state = Fsm.ActiveStateName ?? "";
                if (LiftRideStates.TryGetValue(state, out var target)) {
                    return target >= 0 ? target : IsDown.Value ? 1 : 0;
                }

                if (LiftStandingStates.TryGetValue(state, out var standing)) {
                    return standing;
                }

                return Transform.position.y >= GetMidHeight() ? 1 : 0;
            }
        }

        /// <inheritdoc />
        public override bool HasStop(int stop) => stop is 0 or 1;

        /// <inheritdoc />
        public override bool Contains(Vector3 position) {
            if (Platform == null) {
                return false;
            }

            var bounds = Platform.bounds;
            return position.x >= bounds.min.x - 0.3f && position.x <= bounds.max.x + 0.3f &&
                   position.y >= bounds.max.y - 0.5f && position.y <= bounds.max.y + 3.5f;
        }

        /// <inheritdoc />
        public override bool IsAtStop(int stop, Vector3 position) {
            return Mathf.Abs(position.x - Transform.position.x) <= LiftShaftReach &&
                   (stop == 1 ? position.y >= GetMidHeight() : position.y < GetMidHeight());
        }

        /// <inheritdoc />
        public override bool Move(int stop, bool camera, float skippedDelay) {
            if (IsMoving) {
                if (Stop != stop) {
                    // Turning around goes through the state that picks the way, which also lets the lift carry the hero
                    IsDown.Value = stop == 1;
                    Fsm.SetState(LiftDirectionState);
                }

                return true;
            }

            if (!IsStanding || Stop == stop) {
                return false;
            }

            IsDown.Value = stop == 1;
            Fsm.SetState(skippedDelay >= LiftBobSkip ? LiftDirectionState : LiftBobState);
            return IsMoving;
        }

        /// <inheritdoc />
        public override void PlaceAt(int stop, float value) {
            // A lift that is locked or didn't set itself up yet keeps its own state
            var state = stop == 1 ? "Start Up" : "Start Down";
            if ((IsStanding || IsMoving) && Fsm.GetState(state) != null) {
                Fsm.SetState(state);
            }
        }

        /// <inheritdoc />
        public override void JoinRide(int stop, float value) {
            if (!IsStanding && !IsMoving) {
                return;
            }

            SetHeight(value);
            IsDown.Value = stop == 1;
            Fsm.SetState(LiftDirectionState);
        }


        /// <summary>
        /// Gets the height between the ends of the lift that tells at which end a player is.
        /// </summary>
        private float GetMidHeight() {
            return MidHeight is { } mid && mid.Value != 0f ? mid.Value : Transform.position.y;
        }
    }

    /// <summary>
    /// The one-time story lift, which the sync only holds until both players are in it and whose rides are left to its
    /// FSM and the levers that both games replay.
    /// </summary>
    private class StoryLift : SyncedLift {
        /// <summary>
        /// The FSM of the lift.
        /// </summary>
        public required Fsm Fsm { get; init; }

        /// <summary>
        /// The space in which the lift detects the hero, relative to the position of the lift.
        /// </summary>
        public required Rect Detector { get; init; }

        /// <summary>
        /// Whether the local player was told that the lift waits for the partner.
        /// </summary>
        public bool WaitShown { get; set; }

        /// <inheritdoc />
        public override MonoBehaviour Owner => Fsm.Owner;

        /// <inheritdoc />
        public override string FsmName => Fsm.Name;

        /// <inheritdoc />
        public override bool IsMoving => Fsm.ActiveStateName is { } state && StoryLiftRideStates.Contains(state);

        /// <inheritdoc />
        public override int Stop => 0;

        /// <inheritdoc />
        public override bool HasStop(int stop) => false;

        /// <inheritdoc />
        public override bool Contains(Vector3 position) {
            var origin = Transform.position;
            return Detector.Contains(new Vector2(position.x - origin.x, position.y - origin.y));
        }

        /// <inheritdoc />
        public override bool IsAtStop(int stop, Vector3 position) => false;

        /// <inheritdoc />
        public override bool Move(int stop, bool camera, float skippedDelay) => false;

        /// <inheritdoc />
        public override void PlaceAt(int stop, float value) {
        }

        /// <inheritdoc />
        public override void JoinRide(int stop, float value) {
        }
    }

    /// <summary>
    /// Registers the hooks of lifts that an FSM runs.
    /// </summary>
    private void RegisterFsmLiftHooks() {
        AddLiftHook(
            typeof(Fsm).GetMethod("ProcessEvent", InstanceFlags, null, [typeof(FsmEvent), typeof(FsmEventData)], null),
            new Action<Action<Fsm, FsmEvent, FsmEventData>, Fsm, FsmEvent, FsmEventData>(OnLiftProcessEvent)
        );
    }

    /// <summary>
    /// Forgets the FSMs of lifts of the room that the local player left.
    /// </summary>
    private void ResetFsmLifts() {
        _liftFsms.Clear();
    }

    /// <summary>
    /// Hook for <see cref="Fsm"/>.ProcessEvent, which holds the events that start rides of platform lifts until the
    /// game that decides lets them start, and holds the start of the story lift until both players are in it.
    /// </summary>
    private void OnLiftProcessEvent(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        if (_liftReplaying || fsmEvent == null || _checkedWith == null ||
            self.Name != LiftFsmName && self.Name != StoryLiftFsmName) {
            orig(self, fsmEvent!, eventData);
            return;
        }

        try {
            var from = self.ActiveStateName;
            if (GetTransitionTarget(self.ActiveState, fsmEvent) is { } to) {
                if (self.Name == StoryLiftFsmName) {
                    if (from == StoryLiftIdleState && to == StoryLiftEnteredState && HoldStoryLift(self)) {
                        return;
                    }
                } else if (GetFsmLift(self) is FsmLift lift && to == LiftBobState && lift.IsStanding &&
                           HoldFsmLiftRide(lift, fsmEvent.Name, lift.Stop == 0 ? 1 : 0)) {
                    return;
                }
            }
        } catch (Exception e) {
            LogLiftError(e);
        }

        // A ride that the event starts reaches the partner once the FSM switched to it, which an event that the FSM
        // sends itself only does after its actions ran (see UpdateFsmLiftRide)
        orig(self, fsmEvent, eventData);
    }

    /// <summary>
    /// Gets the state that an event leads to from a state, or null if the state doesn't listen for it.
    /// </summary>
    private static string? GetTransitionTarget(FsmState? state, FsmEvent fsmEvent) {
        if (state?.Transitions == null) {
            return null;
        }

        foreach (var transition in state.Transitions) {
            if (transition.FsmEvent == fsmEvent || transition.EventName == fsmEvent.Name) {
                return transition.ToState;
            }
        }

        return null;
    }

    /// <summary>
    /// Decides about a ride of a platform lift that the local player starts: returns false to let it start at once, or
    /// holds it back, sending it to the game that decides or letting it wait for its turn.
    /// </summary>
    private bool HoldFsmLiftRide(FsmLift lift, string eventName, int stop) {
        // Only the hero touching the lift or waiting at the other end is a player's call; other events are scripted
        if (GetLiftPartner() is not { } partner || eventName is not ("TOUCHED" or "CANCEL") ||
            lift.Fsm.Variables.FindFsmBool("Force Up") is { Value: true } ||
            lift.Fsm.Variables.FindFsmBool("Funeral Happening") is { Value: true }) {
            return false;
        }

        var hero = HeroController.instance;
        var inside = hero != null && lift.ContainsHero(hero);

        // Alone, the lift comes for a hero anywhere at the other end. With two players that only counts close to its
        // shaft, or a partner far away would send it away from the other player again and again. A hero who stood at
        // that stop while the lift left without them calls it again once they left the stop and came back
        if (!inside && (hero == null || !lift.IsAtStop(stop, hero.transform.position) || lift.DeclinedStop == stop)) {
            return true;
        }

        if (DecidesLifts(partner) && !IsLiftHeldForOther(lift, false, partner)) {
            return false;
        }

        RequestLiftRide(lift, stop, inside, true, partner);
        return true;
    }

    /// <summary>
    /// Follows the state of a platform lift every frame: sends a ride that its FSM started or turned around by itself,
    /// and remembers a stop that the lift left while the local hero stood there without riding it.
    /// </summary>
    private void UpdateFsmLiftRide(FsmLift lift, ClientPlayerData? partner) {
        var hero = HeroController.instance;
        if (lift.IsStanding) {
            lift.StoodAt = lift.Stop;
        } else if (lift.IsMoving && lift.StoodAt >= 0) {
            if (hero != null && !lift.ContainsHero(hero) && lift.IsAtStop(lift.StoodAt, hero.transform.position)) {
                lift.DeclinedStop = lift.StoodAt;
            }

            lift.StoodAt = -1;
        }

        if (lift.DeclinedStop >= 0 && (hero == null || !lift.IsAtStop(lift.DeclinedStop, hero.transform.position))) {
            lift.DeclinedStop = -1;
        }

        // Any ride under way that the partner has not been told where it is going. This used to compare the state
        // the lift was in a frame ago against a list of the states a ride begins from, which recognised a ride only
        // when it began one particular way. It missed a lift being unlocked, a lift turning around through the state
        // that picks a direction, and - the one that cost a room - a lift being put where the partner says theirs
        // stands: doing that runs the lift's own state machine, inside that one call, far enough to set it off back
        // towards whoever is standing below, so by the time anything looked it was several states past the list.
        if (partner == null || !lift.IsMoving || lift.Stop == lift.ToldPartnerStop) {
            return;
        }

        lift.WasMoving = true;
        lift.Calls.RemoveAll(call => call.Stop == lift.Stop);
        SendLiftMove(lift, lift.Stop, lift.Transform.position.y, partner.Id);
    }

    /// <summary>
    /// Gets the sync of a platform lift or the story lift of an FSM, looking at the FSM the first time.
    /// </summary>
    private SyncedLift? GetFsmLift(Fsm fsm) {
        if (_liftFsms.TryGetValue(fsm, out var known)) {
            return known;
        }

        SyncedLift? lift = null;
        if (fsm.GameObject is { } owner && fsm.Owner != null) {
            if (fsm.Name == LiftFsmName && owner.GetComponent<LiftPlatform>() != null &&
                fsm.GetState(LiftBobState) != null && fsm.Variables.FindFsmBool("Is Down") is { } isDown) {
                lift = new FsmLift {
                    Path = ScenePath.Get(owner.transform),
                    Fsm = fsm,
                    IsDown = isDown,
                    MidHeight = fsm.Variables.FindFsmFloat("Hero Mid Y"),
                    Platform = owner.GetComponent<Collider2D>()
                };
            } else if (fsm.Name == StoryLiftFsmName && fsm.GetState("Break Wait 3") != null &&
                       fsm.Variables.FindFsmGameObject("Hero Detector")?.Value is { } detector &&
                       detector.TryGetComponent<Collider2D>(out var collider)) {
                var bounds = collider.bounds;
                var origin = owner.transform.position;
                lift = new StoryLift {
                    Path = ScenePath.Get(owner.transform),
                    Fsm = fsm,
                    Detector = new Rect(
                        bounds.min.x - origin.x - 0.5f, bounds.min.y - origin.y - 0.5f, bounds.size.x + 1f,
                        bounds.size.y + 1f
                    )
                };
            }
        }

        _liftFsms[fsm] = lift;
        if (lift != null) {
            lift.WasMoving = lift.IsMoving;
            if (lift is FsmLift fsmLift) {
                fsmLift.StoodAt = fsmLift.IsStanding ? fsmLift.Stop : -1;
            }

            _lifts[fsm] = lift;
        }

        return lift;
    }

    /// <summary>
    /// Adds the platform lifts of the current room.
    /// </summary>
    private void AddRoomFsmLifts(List<SyncedLift> lifts) {
        foreach (var platform in Object.FindObjectsByType<LiftPlatform>(FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None)) {
            foreach (var component in platform.GetComponents<PlayMakerFSM>()) {
                if (component.FsmName == LiftFsmName && GetFsmLift(component.Fsm) is FsmLift lift) {
                    lifts.Add(lift);
                }
            }
        }
    }

    /// <summary>
    /// Holds the start of the story lift until the hero and the avatar of the partner are both in it.
    /// </summary>
    /// <returns>Whether the start is held.</returns>
    private bool HoldStoryLift(Fsm fsm) {
        if (GetFsmLift(fsm) is not StoryLift lift || IsBothInStoryLift(lift)) {
            return false;
        }

        if (!lift.WaitShown) {
            lift.WaitShown = true;
            var name = _checkedWith is { } id && _playerData.TryGetValue(id, out var partner)
                ? partner.Username
                : "your partner";
            Chat(Lang.Pick($"Waiting for {name} to ride this lift together", $"正在等 {name} 一起坐这台升降台"));
        }

        return true;
    }

    /// <summary>
    /// Starts the story lifts that were held once both players are in them.
    /// </summary>
    private void UpdateStoryLifts(ClientPlayerData partner) {
        foreach (var lift in _lifts.Values) {
            if (lift is not StoryLift story || story.Owner == null ||
                story.Fsm.ActiveStateName != StoryLiftIdleState) {
                continue;
            }

            var hero = HeroController.instance;
            if (hero == null || !story.ContainsHero(hero)) {
                story.WaitShown = false;
                continue;
            }

            if (!partner.IsInLocalScene || !IsBothInStoryLift(story)) {
                continue;
            }

            _liftReplaying = true;
            try {
                story.Fsm.SetState(StoryLiftEnteredState);
            } finally {
                _liftReplaying = false;
            }
        }
    }

    /// <summary>
    /// Whether the hero and the avatar of the partner are both in the story lift.
    /// </summary>
    private bool IsBothInStoryLift(StoryLift lift) {
        var hero = HeroController.instance;
        return hero != null && lift.ContainsHero(hero) && GetLiftPartner()?.PlayerContainer is { } container &&
               lift.Contains(container.transform.position);
    }
}
