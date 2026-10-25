using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Util;
using UnityEngine;
using Bounds = UnityEngine.Bounds;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// The passages of dropping boxes, whose controller drops its boxes one after another in an order that goes by where
/// the player is: from the end of the passage that the player came to, or from the side of the controller that they
/// stand on, so that the box in front of them drops first and the rest follow them across. In a game with two players
/// it went by either player, or by the player of the game that runs the room alone: a player at one end turned the
/// order round for the one waiting at the other, who then found the box in front of them gone as the debris poured,
/// again and again, and couldn't get across at all (USER 10-24, "箱子和石流会掉的越来越同步，最后完全一起落下过不去了").
///
/// With two players in the room the order keeps to the side it is set for, and turns as a player is put back at a safe
/// place after a hazard near the boxes, or stands up there after a rescue, to the side of the boxes that they are put
/// on (USER 10-25: "只要有人掉血了回到安全点就把机关调到她的位置。这样虽然第一次不一定合理，但是后续都是合理的",
/// "做吧"), and as both players come to the same end, or the same side of a controller that goes by sides, which
/// leaves nobody waiting at the other. The side that a player comes to first sets it, as in the game, and a player at
/// the other end no longer turns it, so whoever finds it set for the other side gets it after one try, and keeps it.
/// Players wait at a safe place until the order is theirs and only then cross, so the order went by nobody when it
/// went by the player among the boxes, and when it went by where the players stood it kept to a player who had crossed
/// already and waited on the far side (USER 10-25, "不存在你说的正在过状态"). With one player in the room, a player at
/// an end, or on a side, turns it again, as in the game, and so does their being put back, which only ever sets it for
/// the side they wait on: the partner may have left the room with it set for theirs. The game that runs the room
/// decides, and the other follows its rounds (see CoopSave.PacedMachines); every game hears of every put-back in the
/// room, so the one that takes the room over knows the order as well.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How the controllers of dropping boxes are called.
    /// </summary>
    private const string BoxControllerName = "Grind Plat Control";

    /// <summary>
    /// The least time between two lines of the log about a passage keeping its order while a player stands at its
    /// other end, in seconds.
    /// </summary>
    private const float BoxPassageLogInterval = 5f;

    /// <summary>
    /// How far beside the boxes of a controller a player may be put back for its order to turn to them, sideways: as
    /// far as the safe places of the rooms with such controllers lie from the boxes, and their outer ones a little
    /// farther.
    /// </summary>
    private const float BoxPutBackReach = 25f;

    /// <summary>
    /// The passages of dropping boxes in the loaded rooms, each found the first time its controller asks one of its
    /// ranges, or which side of it the player stands on, which it does from the moment its room is loaded.
    /// </summary>
    private static readonly List<BoxPassage> BoxPassages = [];

    /// <summary>
    /// The passage that each range that was asked about is an end of, or null for a range at the end of none.
    /// </summary>
    private static readonly ConditionalWeakTable<AlertRange, StrongBox<BoxPassage?>> BoxPassageEnds = new();

    /// <summary>
    /// The passage of each state machine that looked at which side of it the player stands on, or null for a state
    /// machine that is not the controller of dropping boxes.
    /// </summary>
    private static readonly ConditionalWeakTable<HutongGames.PlayMaker.Fsm, StrongBox<BoxPassage?>>
        BoxControllerMachines = new();

    /// <summary>
    /// Whether looking at which side of a controller of dropping boxes the player stands on threw, which is only
    /// logged once.
    /// </summary>
    private static bool _boxSideFailed;

    /// <summary>
    /// What a controller of dropping boxes that goes by the side of it that the player stands on is told to look at,
    /// with two players in the room: an object put on the side that its order is set for. Made the first time it is
    /// needed, kept across rooms, and never switched on.
    /// </summary>
    private static GameObject? _boxSideStandIn;

    /// <summary>
    /// Hook for an action looking at which side of its object a player stands on.
    /// </summary>
    private Hook? _checkTargetDirectionHook;

    /// <summary>
    /// A passage of dropping boxes: a controller that goes by the two ranges at its ends, or by the side of it that the
    /// player stands on.
    /// </summary>
    private sealed class BoxPassage {
        public BoxPassage(GameObject controller, AlertRange[]? ends, float boxesLeft, float boxesRight) {
            Controller = controller;
            Ends = ends;
            BoxesLeft = boxesLeft;
            BoxesRight = boxesRight;
        }

        /// <summary>
        /// The controller, whose state machine asks the ranges.
        /// </summary>
        public GameObject Controller { get; }

        /// <summary>
        /// The ranges at the ends, the left one first, or null for a controller that goes by the side of it that the
        /// player stands on.
        /// </summary>
        public AlertRange[]? Ends { get; }

        /// <summary>
        /// Where the boxes and chutes of the controller begin, from the left.
        /// </summary>
        public float BoxesLeft { get; }

        /// <summary>
        /// Where the boxes and chutes of the controller end, at the right.
        /// </summary>
        public float BoxesRight { get; }

        /// <summary>
        /// The side that the order is set for: 0 for the left, 1 for the right, or null while no player has come to an
        /// end, or for a controller that goes by sides, to a side of it, yet.
        /// </summary>
        public int? Way { get; set; }

        /// <summary>
        /// When the next line of the log about it keeping its order while a player stands at its other end may be
        /// written.
        /// </summary>
        public float NextLogTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where the boxes are split into a left and a right side: the middle of the boxes for a passage with ends,
        /// whose ends lie far out, and the controller itself for one that goes by the side of it the player stands on.
        /// </summary>
        public float Middle => Ends != null ? (BoxesLeft + BoxesRight) / 2f : Controller.transform.position.x;

        /// <summary>
        /// Sets the side that the order is set for, saying so in the log when it changes.
        /// </summary>
        /// <param name="side">The side: 0 for the left, 1 for the right.</param>
        /// <param name="why">Why, for the log.</param>
        public void SetWay(int side, string why) {
            if (Way == side) {
                return;
            }

            Way = side;
            Logger.Info($"[Boxes] '{Controller.name}' drops its boxes for the {SideName(side)}, {why}");
        }

        /// <summary>
        /// Forgets the order, for a passage that is taken up again after it was let go of.
        /// </summary>
        public void Forget() {
            Way = null;
            NextLogTime = float.NegativeInfinity;
        }
    }

    /// <summary>
    /// Registers what keeps the passages of dropping boxes to the side they are set for.
    /// </summary>
    private void RegisterBoxPassageHooks() {
        _checkTargetDirectionHook = TryCreateHook(
            typeof(CheckTargetDirection), "DoCheckDirection",
            new Action<Action<CheckTargetDirection>, CheckTargetDirection>(OnCheckTargetDirection)
        );
    }

    /// <summary>
    /// Disposes what keeps the passages of dropping boxes to the side they are set for. A passage is taken up again,
    /// afresh, the next time its controller asks (see <see cref="FindBoxPassage"/> and <see cref="FindBoxController"/>).
    /// </summary>
    private void DisposeBoxPassageHooks() {
        _checkTargetDirectionHook?.Dispose();
        _checkTargetDirectionHook = null;

        BoxPassages.Clear();
    }

    /// <summary>
    /// A player was put back at a safe place after a hazard, or stood up there after a rescue, in this game or in
    /// another one of the room: the passages of dropping boxes near there turn their order to the side of the boxes
    /// that the place is on (see the class).
    /// </summary>
    /// <param name="scene">The room it was in.</param>
    /// <param name="x">Where the place is, sideways.</param>
    /// <param name="who">Who was put there, for the log.</param>
    internal static void NoteBoxPutBack(string scene, float x, string who) {
        for (var index = BoxPassages.Count - 1; index >= 0; index--) {
            var passage = BoxPassages[index];
            if (passage.Controller == null) {
                BoxPassages.RemoveAt(index);
                continue;
            }

            if (x >= passage.BoxesLeft - BoxPutBackReach && x <= passage.BoxesRight + BoxPutBackReach &&
                passage.Controller.scene.name == scene) {
                passage.SetWay(x < passage.Middle ? 0 : 1, $"as {who} was put back on that side");
            }
        }
    }

    /// <summary>
    /// Answers a range at an end of a passage of dropping boxes by the side that its order is set for, which the state
    /// machine of the controller then keeps to or turns to (see the class). A player in the range sets it for that
    /// side while nobody came to an end yet, and with one player in the room whenever they come to one, as in the
    /// game; with two, once both are in it.
    /// </summary>
    /// <param name="range">The range.</param>
    /// <param name="answer">Whether the range is to say a player stands in it.</param>
    /// <returns>Whether the range is an end of a passage, answered so; otherwise it is answered as before.</returns>
    private static bool TryAnswerBoxPassageEnd(AlertRange range, out bool answer) {
        answer = false;
        if (FindBoxPassage(range) is not { Ends: { } ends } passage) {
            return false;
        }

        var end = ends[0] == range ? 0 : 1;
        var anyone = IsAnyPlayerInsideAlertRange(range);
        if (anyone && passage.Way != end) {
            if (passage.Way == null) {
                passage.SetWay(end, "as a player came to that end first");
            } else if (PlayerTargetRegistry.GetTrackedPlayers().Count < 2) {
                passage.SetWay(end, "as the one player in the room came to that end");
            } else if (AreAllPlayersInsideAlertRange(range)) {
                passage.SetWay(end, "as every player in the room came to that end");
            }
        }

        if (passage.Way is not { } way) {
            return true;
        }

        answer = way == end;

        // Said when a player at an end doesn't turn it, which is the whole of what this changes there
        if (anyone && !answer && Time.unscaledTime >= passage.NextLogTime) {
            passage.NextLogTime = Time.unscaledTime + BoxPassageLogInterval;
            Logger.Info(
                $"[Boxes] '{passage.Controller.name}' keeps its boxes for the {SideName(way)}, with a player at its " +
                $"{SideName(end)} end"
            );
        }

        return true;
    }

    /// <summary>
    /// Whether every tracked player stands inside a range and can be seen from it, of whom there is at least one.
    /// </summary>
    private static bool AreAllPlayersInsideAlertRange(AlertRange range) {
        var players = PlayerTargetRegistry.GetTrackedPlayers();
        if (players.Count == 0) {
            return false;
        }

        foreach (var player in players) {
            if (player == null || !IsPlayerInsideAlertRange(range, player) ||
                !HasLineOfSightToAlertRangeTarget(range, player)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a range is at an end of a passage of dropping boxes, which may answer that nobody is in it while a
    /// player waits there.
    /// </summary>
    private static bool IsBoxPassageEnd(AlertRange range) {
        return FindBoxPassage(range) is { Ends: not null };
    }

    /// <summary>
    /// Finds the passage of dropping boxes that a range is an end of: a range under a controller of dropping boxes that
    /// has two ranges under it, one at each end. A passage that was let go of while this game was not connected is
    /// taken up again afresh.
    /// </summary>
    private static BoxPassage? FindBoxPassage(AlertRange range) {
        if (BoxPassageEnds.TryGetValue(range, out var known)) {
            return TakeUp(known.Value);
        }

        BoxPassage? passage = null;
        var parent = range.transform.parent;
        if (parent != null && parent.name.StartsWith(BoxControllerName, StringComparison.Ordinal)) {
            passage = FindPassageOf(parent.gameObject) ?? MakeBoxPassage(parent);
        }

        BoxPassageEnds.Add(range, new StrongBox<BoxPassage?>(passage));
        return passage;
    }

    /// <summary>
    /// Finds the passage of dropping boxes of a state machine that looks at which side of it the player stands on, if
    /// it is a controller of dropping boxes. A passage that was let go of while this game was not connected is taken
    /// up again afresh.
    /// </summary>
    private static BoxPassage? FindBoxController(HutongGames.PlayMaker.Fsm fsm) {
        if (BoxControllerMachines.TryGetValue(fsm, out var known)) {
            return TakeUp(known.Value);
        }

        BoxPassage? passage = null;
        if (fsm.GameObject is { } owner && owner != null &&
            owner.name.StartsWith(BoxControllerName, StringComparison.Ordinal)) {
            passage = FindPassageOf(owner) ?? MakeBoxController(owner.transform);
        }

        BoxControllerMachines.Add(fsm, new StrongBox<BoxPassage?>(passage));
        return passage;
    }

    /// <summary>
    /// A passage found before, if its controller is still there, added back afresh if it was let go of.
    /// </summary>
    private static BoxPassage? TakeUp(BoxPassage? passage) {
        if (passage == null || passage.Controller == null) {
            return null;
        }

        if (!BoxPassages.Contains(passage)) {
            passage.Forget();
            BoxPassages.Add(passage);
        }

        return passage;
    }

    /// <summary>
    /// The passage of a controller that is known already, if any.
    /// </summary>
    private static BoxPassage? FindPassageOf(GameObject controller) {
        foreach (var passage in BoxPassages) {
            if (passage.Controller == controller) {
                return passage;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes the passage of a controller of dropping boxes from the two ranges under it, if it has two that lie apart,
    /// with its boxes and chutes, the other objects under it, between them.
    /// </summary>
    private static BoxPassage? MakeBoxPassage(Transform controller) {
        var ranges = new List<(AlertRange Range, Bounds Bounds)>();
        var boxesLeft = float.PositiveInfinity;
        var boxesRight = float.NegativeInfinity;
        for (var index = 0; index < controller.childCount; index++) {
            var child = controller.GetChild(index);
            if (child.TryGetComponent<AlertRange>(out var range)) {
                if (TryGetRangeBounds(range, out var bounds)) {
                    ranges.Add((range, bounds));
                }

                continue;
            }

            var x = child.position.x;
            boxesLeft = Mathf.Min(boxesLeft, x);
            boxesRight = Mathf.Max(boxesRight, x);
        }

        if (ranges.Count != 2) {
            return null;
        }

        if (ranges[0].Bounds.center.x > ranges[1].Bounds.center.x) {
            ranges.Reverse();
        }

        var (left, right) = (ranges[0], ranges[1]);
        var innerLeft = left.Bounds.max.x;
        var innerRight = right.Bounds.min.x;
        if (innerLeft >= innerRight) {
            return null;
        }

        // Boxes outside the passage, or none, leave the whole passage to cross
        boxesLeft = Mathf.Clamp(boxesLeft, innerLeft, innerRight);
        boxesRight = Mathf.Clamp(boxesRight, innerLeft, innerRight);
        if (boxesLeft > boxesRight) {
            (boxesLeft, boxesRight) = (innerLeft, innerRight);
        }

        var passage = new BoxPassage(controller.gameObject, [left.Range, right.Range], boxesLeft, boxesRight);
        BoxPassages.Add(passage);
        Logger.Info(
            $"[Boxes] '{controller.name}' drops its boxes between '{left.Range.name}' and '{right.Range.name}' " +
            $"({innerLeft:0.#} to {innerRight:0.#}, boxes {boxesLeft:0.#} to {boxesRight:0.#}), for the side it is " +
            "set for while two players are in the room"
        );
        return passage;
    }

    /// <summary>
    /// Makes the passage of a controller of dropping boxes that goes by the side of it that the player stands on, with
    /// its boxes, the objects under it.
    /// </summary>
    private static BoxPassage MakeBoxController(Transform controller) {
        var boxesLeft = controller.position.x;
        var boxesRight = boxesLeft;
        for (var index = 0; index < controller.childCount; index++) {
            var x = controller.GetChild(index).position.x;
            boxesLeft = Mathf.Min(boxesLeft, x);
            boxesRight = Mathf.Max(boxesRight, x);
        }

        var passage = new BoxPassage(controller.gameObject, null, boxesLeft, boxesRight);
        BoxPassages.Add(passage);
        Logger.Info(
            $"[Boxes] '{controller.name}' drops its boxes by the side of it ({controller.position.x:0.#}, boxes " +
            $"{boxesLeft:0.#} to {boxesRight:0.#}) that it is set for while two players are in the room"
        );
        return passage;
    }

    /// <summary>
    /// The bounds of the switched-on colliders of a range.
    /// </summary>
    private static bool TryGetRangeBounds(AlertRange range, out Bounds bounds) {
        bounds = default;
        var found = false;
        foreach (var collider in range.GetComponents<Collider2D>()) {
            if (collider == null || !collider.enabled) {
                continue;
            }

            if (found) {
                bounds.Encapsulate(collider.bounds);
            } else {
                bounds = collider.bounds;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Hook for an action looking at which side of its object a player stands on, which for the controller of dropping
    /// boxes that goes by the side of it that its game's player stands on looks at what stands on the side that its
    /// order is set for (see the class).
    /// </summary>
    private static void OnCheckTargetDirection(Action<CheckTargetDirection> orig, CheckTargetDirection self) {
        FsmGameObject? target = null;
        GameObject? lookAt = null;
        try {
            target = self.target;
            var hero = HeroController.SilentInstance;
            if (target != null && hero != null && target.Value == hero.gameObject && self.Fsm is { } fsm &&
                FindBoxController(fsm) is { } passage) {
                lookAt = BoxSideToLookAt(passage);
            }
        } catch (Exception e) {
            if (!_boxSideFailed) {
                _boxSideFailed = true;
                Logger.Warn($"Could not find the side a controller of dropping boxes goes by: {e}");
            }
        }

        if (target == null || lookAt == null || lookAt == target.Value) {
            orig(self);
            return;
        }

        self.target = new FsmGameObject { Value = lookAt };
        try {
            orig(self);
        } finally {
            self.target = target;
        }
    }

    /// <summary>
    /// What a controller of dropping boxes that goes by the side of it that the player stands on is to look at, having
    /// set the side that its order is set for: with one player in the room, that player, whose side it is, as in the
    /// game; with two, the side that both stand on, else the side it was set for, else the side of the player nearest
    /// to it, and an object put on that side.
    /// </summary>
    private static GameObject? BoxSideToLookAt(BoxPassage passage) {
        var players = PlayerTargetRegistry.GetTrackedPlayers();
        var middle = passage.Middle;
        if (players.Count == 0) {
            return null;
        }

        if (players.Count == 1) {
            var only = players[0];
            if (only != null) {
                passage.SetWay(
                    only.transform.position.x < middle ? 0 : 1, "as the one player in the room stands on that side"
                );
            }

            return only;
        }

        int? together = null;
        foreach (var player in players) {
            if (player == null) {
                continue;
            }

            var side = player.transform.position.x < middle ? 0 : 1;
            together = together == null || together == side ? side : -1;
        }

        if (together is 0 or 1) {
            passage.SetWay(together.Value, "as every player in the room stands on that side");
        } else if (passage.Way == null && NearestPlayerSideways(middle) is { } nearest) {
            passage.SetWay(nearest.transform.position.x < middle ? 0 : 1, "as the player nearest to it stands there");
        }

        if (passage.Way is not { } way) {
            return null;
        }

        if (_boxSideStandIn == null) {
            _boxSideStandIn = new GameObject("SSMP Box Side Stand-in") { hideFlags = HideFlags.HideAndDontSave };
            _boxSideStandIn.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_boxSideStandIn);
        }

        var controller = passage.Controller.transform.position;
        _boxSideStandIn.transform.position = new Vector3(controller.x + (way == 0 ? -10f : 10f), controller.y);
        return _boxSideStandIn;
    }

    /// <summary>
    /// The tracked player whose x is nearest to the given one, or null for none.
    /// </summary>
    private static GameObject? NearestPlayerSideways(float x) {
        GameObject? nearest = null;
        var nearestDistance = float.PositiveInfinity;
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player == null) {
                continue;
            }

            var distance = Mathf.Abs(player.transform.position.x - x);
            if (distance < nearestDistance) {
                nearest = player;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The name of a side of a passage, for the log.
    /// </summary>
    private static string SideName(int side) {
        return side == 0 ? "left" : "right";
    }
}
