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
/// the player is: from the end of the passage that the player came in at, or from the side of the controller that they
/// stand on, so that the box in front of them drops first and the rest follow them across. In a game with two players
/// it went by either player, or by the player of the game that runs the room alone: a player waiting at one end turned
/// the order round for the one crossing from the other end, who then found the box in front of them gone as the
/// debris poured, again and again, and couldn't get across at all (USER 10-24, "箱子和石流会掉的越来越同步，最后完全
/// 一起落下过不去了"). The order goes by the player who is crossing now (USER 10-25, "修"): the ranges at the ends of a
/// passage answer as though the players among its boxes and chutes stood at the end of the side they came in from, and
/// nobody at the other end, so that the players waiting count for nothing; with nobody among the boxes, as though the
/// players coming up to them, between the ends, stood at the end of their side; players coming in from both sides keep
/// the order as it is; and with nobody between the ends, either player at an end is an answer, as before. A controller
/// that goes by the side of it that the player stands on goes by the player nearest to it, who is the one on its boxes.
/// The game that runs the room decides, and the other follows its rounds (see CoopSave.PacedMachines).
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How the controllers of dropping boxes are called.
    /// </summary>
    private const string BoxControllerName = "Grind Plat Control";

    /// <summary>
    /// The least time between two lines of the log about the order of a passage going by the player crossing it, in
    /// seconds.
    /// </summary>
    private const float BoxPassageLogInterval = 5f;

    /// <summary>
    /// The passages of dropping boxes in the loaded rooms, each found the first time its controller asks one of its
    /// ranges, which it does from the moment its room is loaded.
    /// </summary>
    private static readonly List<BoxPassage> BoxPassages = [];

    /// <summary>
    /// The passage that each range that was asked about is an end of, or null for a range at the end of none.
    /// </summary>
    private static readonly ConditionalWeakTable<AlertRange, StrongBox<BoxPassage?>> BoxPassageEnds = new();

    /// <summary>
    /// Whether each state machine that looked at which side of it the player stands on is the controller of dropping
    /// boxes.
    /// </summary>
    private static readonly ConditionalWeakTable<HutongGames.PlayMaker.Fsm, StrongBox<bool>> BoxControllerMachines =
        new();

    /// <summary>
    /// Whether looking at which side of a controller of dropping boxes the player stands on threw, which is only
    /// logged once.
    /// </summary>
    private static bool _boxSideFailed;

    /// <summary>
    /// Hook for an action looking at which side of its object a player stands on.
    /// </summary>
    private Hook? _checkTargetDirectionHook;

    /// <summary>
    /// A passage of dropping boxes whose controller goes by the two ranges at its ends.
    /// </summary>
    private sealed class BoxPassage {
        public BoxPassage(
            GameObject controller,
            AlertRange left,
            AlertRange right,
            float innerLeft,
            float innerRight,
            float boxesLeft,
            float boxesRight
        ) {
            Controller = controller;
            Ends = [left, right];
            InnerLeft = innerLeft;
            InnerRight = innerRight;
            BoxesLeft = boxesLeft;
            BoxesRight = boxesRight;
        }

        /// <summary>
        /// The controller, whose state machine asks the ranges.
        /// </summary>
        public GameObject Controller { get; }

        /// <summary>
        /// The ranges at the ends, the left one first.
        /// </summary>
        public AlertRange[] Ends { get; }

        /// <summary>
        /// Where the passage begins, at the side of the left range that faces the right one.
        /// </summary>
        public float InnerLeft { get; }

        /// <summary>
        /// Where the passage ends, at the side of the right range that faces the left one.
        /// </summary>
        public float InnerRight { get; }

        /// <summary>
        /// Where the boxes and chutes of the controller begin, from the left.
        /// </summary>
        public float BoxesLeft { get; }

        /// <summary>
        /// Where the boxes and chutes of the controller end, at the right.
        /// </summary>
        public float BoxesRight { get; }

        /// <summary>
        /// The side of the boxes that each player stood on last, by the player: 0 for the left, 1 for the right. A
        /// player among the boxes came in from it.
        /// </summary>
        public Dictionary<GameObject, int> Sides { get; } = new();

        /// <summary>
        /// When the next line of the log about its order going by the player crossing may be written.
        /// </summary>
        public float NextLogTime { get; set; } = float.NegativeInfinity;
    }

    /// <summary>
    /// Registers what makes the passages of dropping boxes go by the player crossing them.
    /// </summary>
    private void RegisterBoxPassageHooks() {
        _checkTargetDirectionHook = TryCreateHook(
            typeof(CheckTargetDirection), "DoCheckDirection",
            new Action<Action<CheckTargetDirection>, CheckTargetDirection>(OnCheckTargetDirection)
        );

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnBoxPassagesUpdate;
    }

    /// <summary>
    /// Disposes what makes the passages of dropping boxes go by the player crossing them. A passage is taken up again,
    /// afresh, the next time its controller asks one of its ranges (see <see cref="FindBoxPassage"/>).
    /// </summary>
    private void DisposeBoxPassageHooks() {
        _checkTargetDirectionHook?.Dispose();
        _checkTargetDirectionHook = null;

        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnBoxPassagesUpdate;
        BoxPassages.Clear();
    }

    /// <summary>
    /// Notes, every frame, the side of the boxes of each passage that each player stands on, which is the side they
    /// came in from once they are among the boxes.
    /// </summary>
    private static void OnBoxPassagesUpdate() {
        if (BoxPassages.Count == 0) {
            return;
        }

        List<GameObject>? players = null;
        for (var index = BoxPassages.Count - 1; index >= 0; index--) {
            var passage = BoxPassages[index];
            if (passage.Controller == null) {
                BoxPassages.RemoveAt(index);
                continue;
            }

            players ??= PlayerTargetRegistry.GetTrackedPlayers();
            foreach (var player in players) {
                if (player == null) {
                    continue;
                }

                var x = player.transform.position.x;
                var side = x < passage.BoxesLeft ? 0 : x > passage.BoxesRight ? 1 : -1;
                if (side < 0 || passage.Sides.TryGetValue(player, out var last) && last == side) {
                    continue;
                }

                passage.Sides[player] = side;
                Logger.Info(
                    $"[Boxes] {DescribePlayer(player)} is on the {(side == 0 ? "left" : "right")} of the boxes of " +
                    $"'{passage.Controller.name}'"
                );
            }
        }
    }

    /// <summary>
    /// Answers a range at an end of a passage of dropping boxes by the player crossing the passage (see the class), if
    /// anybody is between its ends.
    /// </summary>
    /// <param name="range">The range.</param>
    /// <param name="answer">Whether the range is to say a player stands in it.</param>
    /// <returns>Whether the range is answered so; otherwise it is answered as before.</returns>
    private static bool TryAnswerBoxPassageEnd(AlertRange range, out bool answer) {
        answer = false;
        if (FindBoxPassage(range) is not { } passage) {
            return false;
        }

        // The side that the players among the boxes came in from, and the side of the boxes that the players coming
        // up to them stand on; -1 for players from both sides
        int? crossing = null;
        int? coming = null;
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player == null) {
                continue;
            }

            var x = player.transform.position.x;
            if (x >= passage.BoxesLeft && x <= passage.BoxesRight) {
                if (passage.Sides.TryGetValue(player, out var from)) {
                    crossing = crossing == null || crossing == from ? from : -1;
                }
            } else if (x > passage.InnerLeft && x < passage.InnerRight) {
                var side = x < passage.BoxesLeft ? 0 : 1;
                coming = coming == null || coming == side ? side : -1;
            }
        }

        var way = crossing ?? coming;
        if (way == null) {
            return false;
        }

        var end = passage.Ends[0] == range ? 0 : 1;
        answer = way == end;

        // Said when it comes to something else than what either player at an end would have, which is the whole of
        // what this changes
        if (Time.unscaledTime >= passage.NextLogTime && answer != IsAnyPlayerInsideAlertRange(range)) {
            passage.NextLogTime = Time.unscaledTime + BoxPassageLogInterval;
            Logger.Info(
                way < 0
                    ? $"[Boxes] '{passage.Controller.name}' keeps its order, with players coming in from both sides"
                    : $"[Boxes] '{passage.Controller.name}' drops its boxes for the player " +
                      $"{(crossing != null ? "crossing" : "coming")} from the {(way == 0 ? "left" : "right")}, " +
                      "while another player waits"
            );
        }

        return true;
    }

    /// <summary>
    /// Whether a range is at an end of a passage of dropping boxes, which may answer that nobody is in it while a
    /// player waits there.
    /// </summary>
    private static bool IsBoxPassageEnd(AlertRange range) {
        return FindBoxPassage(range) != null;
    }

    /// <summary>
    /// Finds the passage of dropping boxes that a range is an end of: a range under a controller of dropping boxes that
    /// has two ranges under it, one at each end. A passage that was let go of while this game was not connected is
    /// taken up again afresh.
    /// </summary>
    private static BoxPassage? FindBoxPassage(AlertRange range) {
        if (BoxPassageEnds.TryGetValue(range, out var known)) {
            if (known.Value is not { } found || found.Controller == null) {
                return null;
            }

            if (!BoxPassages.Contains(found)) {
                found.Sides.Clear();
                BoxPassages.Add(found);
            }

            return found;
        }

        BoxPassage? passage = null;
        var parent = range.transform.parent;
        if (parent != null && parent.name.StartsWith(BoxControllerName, StringComparison.Ordinal)) {
            foreach (var other in BoxPassages) {
                if (other.Controller == parent.gameObject) {
                    passage = other;
                    break;
                }
            }

            passage ??= MakeBoxPassage(parent);
        }

        BoxPassageEnds.Add(range, new StrongBox<BoxPassage?>(passage));
        return passage;
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

        var passage = new BoxPassage(
            controller.gameObject, left.Range, right.Range, innerLeft, innerRight, boxesLeft, boxesRight
        );
        BoxPassages.Add(passage);
        Logger.Info(
            $"[Boxes] '{controller.name}' drops its boxes for the player crossing between '{left.Range.name}' and " +
            $"'{right.Range.name}' ({innerLeft:0.#} to {innerRight:0.#}, boxes {boxesLeft:0.#} to {boxesRight:0.#})"
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
    /// boxes that goes by the side of it that its game's player stands on looks at the player nearest to it, the one
    /// on its boxes (see the class).
    /// </summary>
    private static void OnCheckTargetDirection(Action<CheckTargetDirection> orig, CheckTargetDirection self) {
        FsmGameObject? target = null;
        GameObject? nearest = null;
        try {
            target = self.target;
            var hero = HeroController.SilentInstance;
            if (target != null && hero != null && target.Value == hero.gameObject && self.Fsm is { } fsm &&
                IsBoxControllerMachine(fsm)) {
                nearest = NearestPlayerSideways(fsm.GameObject.transform.position.x);
            }
        } catch (Exception e) {
            if (!_boxSideFailed) {
                _boxSideFailed = true;
                Logger.Warn($"Could not find the player crossing at a controller of dropping boxes: {e}");
            }
        }

        if (target == null || nearest == null || nearest == target.Value) {
            orig(self);
            return;
        }

        self.target = new FsmGameObject { Value = nearest };
        try {
            orig(self);
        } finally {
            self.target = target;
        }
    }

    /// <summary>
    /// Whether a state machine is that of a controller of dropping boxes.
    /// </summary>
    private static bool IsBoxControllerMachine(HutongGames.PlayMaker.Fsm fsm) {
        if (BoxControllerMachines.TryGetValue(fsm, out var known)) {
            return known.Value;
        }

        var isController = fsm.GameObject is { } owner && owner != null &&
                           owner.name.StartsWith(BoxControllerName, StringComparison.Ordinal);
        BoxControllerMachines.Add(fsm, new StrongBox<bool>(isController));
        return isController;
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
}
