using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The things put out to go after a player by themselves: they follow a player about and then rush at them, like the
/// fireballs that the room lanterns, the effigy and a summoning creature put out. Each game had its own go after that
/// game's own player. Now the game of the scene host picks whom each goes after by the one rule for creatures
/// (GamePatcher.ChooseThrownTarget, D19), as it starts its chase and each time it asks to rush, and its own alone says
/// when one that circles notices a player, when one rushes, and when one dies away or bursts: the other games take
/// those steps on the host's word, with where it was and whom it goes after, so that every game shows the same fireball
/// after the same player, rushing and ending at the same moment (USER 11-14 "按照A吧", D107). A shot that only flies the
/// way it was sent never starts a chase, and keeps the aim of what sent it (D20).
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// The things that chase, each by the name of its prefab.
    /// </summary>
    private static readonly ChaserKind[] ChaserKinds = [
        new(
            "Wisp Fireball", "Control",
            startStateNames: ["Fly Start", "Alert"],
            askStateName: "Request Attack",
            waitStateNames: ["Fly Start", "Follow Hero", "Request Attack", "Follow Start", "Follow", "Alert"],
            heldStateNames: ["Request Attack", "Fire Antic", "Dissipate"],
            noticeStateName: "Alert",
            noticedFromStateNames: ["Follow Start", "Follow"],
            endStateNames: ["Explode", "Water", "End"],
            beforeStateNames: ["Pause", "Init", "To Summon Point"]
        )
    ];

    /// <summary>
    /// The names of the state machines of <see cref="ChaserKinds"/>, which a change of state is asked about at all, as
    /// the hook this serves is called for every change of state in the game.
    /// </summary>
    private static readonly HashSet<string> ChaserFsmNames = BuildChaserFsmNames();

    /// <summary>
    /// How long a word of the scene host's about a thing that chases waits here at most for its thing to be here and
    /// where the word can be taken, in seconds (see <see cref="_earlyChases"/>).
    /// </summary>
    private const float EarlyChaseTime = 1f;

    /// <summary>
    /// The scene host's words about things that chase that came before their thing was here, or before it had come out
    /// far enough to take the step of the word, in the order they came, with when each came: a lantern here starts the
    /// host's round as the word for the round comes, and its fireball comes out here about when the host's word that
    /// it starts its chase does.
    /// </summary>
    private readonly List<(CoopHitUpdate Update, float Since)> _earlyChases = [];

    /// <summary>
    /// The things that chase here that wait for a word of the scene host's to take a step (see
    /// <see cref="ThrownMark.HeldStep"/>), each with the name it had then: a thing that this game runs the room for
    /// takes the step by itself.
    /// </summary>
    private readonly List<(ThrownMark Mark, string Key)> _heldChasers = [];

    /// <summary>
    /// What a change of state of a thing that chases does (see <see cref="OnThrownTransition"/>). In the scene host's
    /// game it picks whom the thing goes after as the thing starts its chase or asks to rush, before the state it goes
    /// to aims at anyone, and tells the others of the pick and of each step that it alone takes; in another game such a
    /// step is held, as the host's word for it comes (see <see cref="ApplyThrownChase"/>).
    /// </summary>
    /// <returns>Whether the change went on.</returns>
    private bool ChaserTransition(
        Func<Fsm, FsmTransition, bool, bool> orig,
        Fsm fsm,
        FsmTransition transition,
        bool isGlobal,
        ThrownMark mark,
        ChaserKind kind
    ) {
        var from = fsm.ActiveStateName;
        var to = transition.ToState;
        var isHost = false;
        var turned = false;
        try {
            isHost = _entityManager.IsSceneHost;
            if (!isHost && kind.IsHeld(from, to, mark.ToldChase)) {
                Hold(mark, to);
                return false;
            }

            mark.HeldStep = null;
            var starts = Array.IndexOf(kind.StartStateNames, to) >= 0;
            if (starts) {
                mark.Chasing = true;
            }

            // With nobody else in the room it goes after this game's player alone, as in a game with one player
            if (isHost && (starts || to == kind.AskStateName) && IsAnyMemberInRoom()) {
                var before = mark.Target;
                var pick = GamePatcher.ChooseThrownTarget(mark.gameObject, starts);
                if (starts || pick != before) {
                    ThrownThings.TurnTo(mark, pick);
                    turned = true;
                }
            }
        } catch (Exception e) {
            SayChaseFailed(e);
        }

        // Exactly once, whatever threw: a hook that throws takes the state machine out of the state it was in
        var changed = orig(fsm, transition, isGlobal);
        if (changed && isHost && mark.Chasing) {
            try {
                var step = kind.IsToldStep(from, to) ? to : "";
                if (turned || step.Length > 0) {
                    SendChase(mark, step);
                }
            } catch (Exception e) {
                SayChaseFailed(e);
            }
        }

        return changed;
    }

    /// <summary>
    /// Has a thing that chases wait here to go into a state until the scene host's word for it comes, or until this
    /// game runs the room (see <see cref="UpdateChasers"/>).
    /// </summary>
    private void Hold(ThrownMark mark, string to) {
        if (mark.HeldStep == to) {
            return;
        }

        if (mark.HeldStep == null) {
            _heldChasers.Add((mark, mark.Key!));
        }

        mark.HeldStep = to;
        Logger.Info($"[Thrown] '{mark.Key}' waits here for the scene host's word to go into '{to}'");
    }

    /// <summary>
    /// Writes down once that picking or sending the chase of a thing threw.
    /// </summary>
    private void SayChaseFailed(Exception e) {
        if (!_thrownFailed) {
            _thrownFailed = true;
            Logger.Error($"Could not pick or send whom a thing that chases goes after:\n{e}");
        }
    }

    /// <summary>
    /// Sends the members in the room whom a thing that chases goes after in this game, the scene host's, with where it
    /// is, and the step it takes by this game's own say, or none.
    /// </summary>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="step">The state it goes into, or "" for whom it goes after alone.</param>
    private void SendChase(ThrownMark mark, string step) {
        if (mark.Key is not { } key || !_netClient.IsConnected) {
            return;
        }

        var code = CodeOfPlayer(mark.Target);
        var update = new CoopHitUpdate {
            Kind = CoopHitKind.ThrownChase,
            Scene = SceneManager.GetActiveScene().name,
            Path = ThrownThings.PathOf(key, mark, mark.gameObject, ThrownThings.PlaceOf(mark)),
            Responder = step,
            Hit = [(byte) code, (byte) (code >> 8)]
        };
        if (SendToRoomMembers(update)) {
            Logger.Info(
                $"[Thrown] '{key}' goes after {DescribeChased(mark.Target)}" +
                (step.Length > 0 ? $" and into '{step}'" : "") + ", as the others are told"
            );
        }
    }

    /// <summary>
    /// Takes the scene host's word about a thing that chases: the twin here is put where the host's was and goes after
    /// whom the host's goes after, and takes the step the host's took if it is where that step is taken from. A word
    /// that comes before its thing, or before its thing came out far enough for the step, waits for it for a moment
    /// (see <see cref="_earlyChases"/>).
    /// </summary>
    /// <param name="update">The word.</param>
    /// <param name="since">When the word came, for one that waited; null for one that has just come.</param>
    private void ApplyThrownChase(CoopHitUpdate update, float? since = null) {
        if (_entityManager.IsSceneHost || update.Hit.Length < 2) {
            return;
        }

        var came = since ?? Time.unscaledTime;
        if (FindPlace(update) is not { } part || !ThrownThings.TryGetMark(part, out var mark) ||
            !FindChaser(mark, out var kind, out var fsm)) {
            WaitForChaser(update, came, "the thing was not here");
            return;
        }

        var step = update.Responder;
        var at = fsm.ActiveStateName;
        if (step.Length > 0 && !kind.TakesFrom(step, at)) {
            if (Array.IndexOf(kind.BeforeStateNames, at) >= 0) {
                WaitForChaser(update, came, $"the thing was still in '{at}'");
                return;
            }

            Logger.Info(
                $"[Thrown] '{mark.Key}' stays in '{at}' rather than go into '{step}' as the scene host's did, as it " +
                "does not go there from there"
            );
            return;
        }

        var code = (ushort) (update.Hit[0] | update.Hit[1] << 8);
        ThrownThings.PutAt(mark, update.Path);
        ThrownThings.TurnTo(mark, FigureOfThrowTarget(code, update.PlayerId));
        mark.Chasing = true;
        mark.ToldChase = true;
        if (step.Length == 0) {
            Logger.Info($"[Thrown] '{mark.Key}' goes after {DescribeChased(mark.Target)}, as the scene host's does");
            return;
        }

        mark.HeldStep = null;
        fsm.SetState(step);
        Logger.Info(
            $"[Thrown] '{mark.Key}' goes into '{step}' after {DescribeChased(mark.Target)}, as the scene host's did"
        );
    }

    /// <summary>
    /// Has a word of the scene host's about a thing that chases wait for the thing for a while longer, or gives it up
    /// once it waited for <see cref="EarlyChaseTime"/>.
    /// </summary>
    /// <param name="update">The word.</param>
    /// <param name="came">When it came.</param>
    /// <param name="why">Why it is not taken now, for the log.</param>
    private void WaitForChaser(CoopHitUpdate update, float came, string why) {
        if (Time.unscaledTime - came < EarlyChaseTime) {
            _earlyChases.Add((update, came));
        } else {
            NotReplayed(update, "chase", $"{why} for {EarlyChaseTime:0.#} s");
        }
    }

    /// <summary>
    /// Every frame: takes the words of the scene host's that waited for their thing (see
    /// <see cref="ApplyThrownChase"/>), in the order they came, and, once this game runs the room, has each thing that
    /// waited for a word take its step by itself, as nobody else will say it.
    /// </summary>
    private void UpdateChasers() {
        if (_earlyChases.Count > 0) {
            var waiting = _earlyChases.ToArray();
            _earlyChases.Clear();
            foreach (var (update, since) in waiting) {
                ApplyThrownChase(update, since);
            }
        }

        if (_heldChasers.Count == 0) {
            return;
        }

        var runsTheRoom = _entityManager.IsSceneHost;
        for (var i = _heldChasers.Count - 1; i >= 0; i--) {
            var (mark, key) = _heldChasers[i];
            var held = mark != null && mark.Key == key ? mark.HeldStep : null;
            if (held != null && !runsTheRoom) {
                continue;
            }

            _heldChasers.RemoveAt(i);
            if (held != null && FindChaser(mark!, out _, out var fsm)) {
                mark!.HeldStep = null;
                fsm.SetState(held);
                Logger.Info($"[Thrown] '{key}' goes into '{held}' by itself, as this game runs the room now");
            }
        }
    }

    /// <summary>
    /// The thing that chases whose state machine changes state, if it is one: a named thrown thing of
    /// <see cref="ChaserKinds"/> whose own state machine of the kind it is. Asked of every change of state of a state
    /// machine of such a name, so it looks at the object of the state machine alone.
    /// </summary>
    private static bool IsChaserTransition(Fsm fsm, out ThrownMark mark, out ChaserKind kind) {
        mark = null!;
        kind = null!;
        return ChaserFsmNames.Contains(fsm.Name) && fsm.FsmComponent is { } machine && machine != null &&
               machine.TryGetComponent(out mark) && mark.Key != null && FindChaser(mark, out kind, out var own) &&
               own == fsm;
    }

    /// <summary>
    /// The kind of thing that chases that a named thrown thing is, by its name, and its state machine of that kind.
    /// </summary>
    private static bool FindChaser(ThrownMark mark, out ChaserKind kind, out Fsm fsm) {
        var name = mark.name;
        foreach (var each in ChaserKinds) {
            if (name != each.PrefabName && !name.StartsWith(each.PrefabName + "(Clone)", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var machine in mark.GetComponents<PlayMakerFSM>()) {
                if (machine.FsmName == each.FsmName && machine.Fsm != null) {
                    kind = each;
                    fsm = machine.Fsm;
                    return true;
                }
            }
        }

        kind = null!;
        fsm = null!;
        return false;
    }

    /// <summary>
    /// Names the player a thing goes after for the log.
    /// </summary>
    private static string DescribeChased(GameObject? player) {
        var hero = HeroController.SilentInstance;
        return player == null ? "no one"
            : hero != null && player == hero.gameObject ? "the local player"
            : $"'{player.name}'";
    }

    /// <summary>
    /// Collects the names of the state machines of <see cref="ChaserKinds"/>.
    /// </summary>
    private static HashSet<string> BuildChaserFsmNames() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in ChaserKinds) {
            names.Add(kind.FsmName);
        }

        return names;
    }

    /// <summary>
    /// One kind of thing that chases.
    /// </summary>
    /// <param name="prefabName">The name of its prefab.</param>
    /// <param name="fsmName">The state machine that runs it.</param>
    /// <param name="startStateNames">The states it starts its chase in.</param>
    /// <param name="askStateName">The state in which it asks to rush.</param>
    /// <param name="waitStateNames">The states it is in before it rushes: following a player, asking to rush, or
    /// circling. A twin in another game takes the steps of the scene host's out of them.</param>
    /// <param name="heldStateNames">The states that a twin in another game, once the host told it of its chase, goes
    /// into out of a state it waits in on the host's word alone: it asks to rush, rushes, or dies away.</param>
    /// <param name="noticeStateName">The state in which one that circles notices a player and starts its chase, which
    /// a twin goes into on the host's word alone.</param>
    /// <param name="noticedFromStateNames">The states in which it circles.</param>
    /// <param name="endStateNames">The states in which it bursts or goes away, which the host's says as well, for a
    /// twin that waits still.</param>
    /// <param name="beforeStateNames">The states it is in as it comes out, before it is in one it waits in.</param>
    private sealed class ChaserKind(
        string prefabName,
        string fsmName,
        string[] startStateNames,
        string askStateName,
        string[] waitStateNames,
        string[] heldStateNames,
        string noticeStateName,
        string[] noticedFromStateNames,
        string[] endStateNames,
        string[] beforeStateNames
    ) {
        public readonly string PrefabName = prefabName;
        public readonly string FsmName = fsmName;
        public readonly string[] StartStateNames = startStateNames;
        public readonly string AskStateName = askStateName;
        public readonly string[] BeforeStateNames = beforeStateNames;

        /// <summary>
        /// Whether a twin in another game than the scene host's goes from one state into another on the host's word
        /// alone: one that circles noticing a player, and, once the host told it of its chase, any step that the host's
        /// says.
        /// </summary>
        /// <param name="from">The state it is in.</param>
        /// <param name="to">The state it would go into.</param>
        /// <param name="told">Whether the host told it of its chase.</param>
        public bool IsHeld(string from, string to, bool told) {
            return to == noticeStateName
                ? Array.IndexOf(noticedFromStateNames, from) >= 0
                : told && Array.IndexOf(waitStateNames, from) >= 0 && Array.IndexOf(heldStateNames, to) >= 0;
        }

        /// <summary>
        /// Whether the scene host's game tells the others of a step its own takes: one that circles noticing a
        /// player, a step out of the states it waits in, and its end.
        /// </summary>
        public bool IsToldStep(string from, string to) {
            if (to == noticeStateName) {
                return Array.IndexOf(noticedFromStateNames, from) >= 0;
            }

            return Array.IndexOf(waitStateNames, from) >= 0 && Array.IndexOf(waitStateNames, to) < 0 ||
                   Array.IndexOf(endStateNames, to) >= 0;
        }

        /// <summary>
        /// Whether a twin takes the step of a word of the host's from the state it is in.
        /// </summary>
        public bool TakesFrom(string step, string at) {
            return Array.IndexOf(step == noticeStateName ? noticedFromStateNames : waitStateNames, at) >= 0;
        }
    }
}
