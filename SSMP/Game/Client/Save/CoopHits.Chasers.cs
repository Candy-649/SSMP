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
/// (GamePatcher.ChooseThrownTarget, D19), as it starts its chase and each time it asks to rush, and it alone says when
/// one rushes, or when one that circles notices a player: the other games hold their own from those steps and take the
/// host's word, with where it was and whom it goes after, so that every game shows the same fireball after the same
/// player, rushing at the same moment (USER 11-14 "按照A吧", D107). A shot that only flies the way it was sent never
/// starts a chase, and keeps the aim of what sent it (D20).
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// The things that chase, each by the name of its prefab.
    /// </summary>
    private static readonly ChaserKind[] ChaserKinds = [
        new(
            "Wisp Fireball", "Control", ["Fly Start", "Alert"], "Request Attack", [
                new ChaserStep("Request Attack", "Fire Antic", ["Fly Start", "Follow Hero", "Request Attack"]),
                new ChaserStep("Follow", "Alert", ["Follow Start", "Follow"])
            ]
        )
    ];

    /// <summary>
    /// The names of the state machines of <see cref="ChaserKinds"/>, which a change of state is asked about at all, as
    /// the hook this serves is called for every change of state in the game.
    /// </summary>
    private static readonly HashSet<string> ChaserFsmNames = BuildChaserFsmNames();

    /// <summary>
    /// How long a word of the scene host's about a thing that chases waits here for its thing at most, in seconds (see
    /// <see cref="_earlyChases"/>).
    /// </summary>
    private const float EarlyChaseTime = 1f;

    /// <summary>
    /// The scene host's words about things that chase that came before their thing was here, in the order they came,
    /// with when each came: a lantern here starts the host's round as the word for the round comes, and its fireball
    /// comes out here about when the host's word that it starts its chase does.
    /// </summary>
    private readonly List<(CoopHitUpdate Update, float Since)> _earlyChases = [];

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
        var to = transition.ToState;
        ChaserStep? step = null;
        var isHost = false;
        var turned = false;
        try {
            step = kind.StepOf(fsm.ActiveStateName, to);
            isHost = _entityManager.IsSceneHost;
            if (step != null && !isHost) {
                if (mark.HeldStep != to) {
                    mark.HeldStep = to;
                    Logger.Info($"[Thrown] '{mark.Key}' waits here for the scene host's word to go into '{to}'");
                }

                return false;
            }

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
        if (changed && isHost && (turned || step != null)) {
            try {
                SendChase(mark, step != null ? to : "");
            } catch (Exception e) {
                SayChaseFailed(e);
            }
        }

        return changed;
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
    /// that comes before its thing waits for it for a moment (see <see cref="_earlyChases"/>).
    /// </summary>
    /// <param name="update">The word.</param>
    /// <param name="since">When the word came, for one that waited; null for one that has just come.</param>
    private void ApplyThrownChase(CoopHitUpdate update, float? since = null) {
        if (_entityManager.IsSceneHost || update.Hit.Length < 2) {
            return;
        }

        if (FindPlace(update) is not { } part || !ThrownThings.TryGetMark(part, out var mark)) {
            var came = since ?? Time.unscaledTime;
            if (Time.unscaledTime - came < EarlyChaseTime) {
                _earlyChases.Add((update, came));
            } else {
                NotReplayed(update, "chase", $"the thing was not here for {EarlyChaseTime:0.#} s");
            }

            return;
        }

        var code = (ushort) (update.Hit[0] | update.Hit[1] << 8);
        ThrownThings.PutAt(mark, update.Path);
        ThrownThings.TurnTo(mark, FigureOfThrowTarget(code, update.PlayerId));
        mark.Chasing = true;
        if (update.Responder.Length == 0) {
            Logger.Info($"[Thrown] '{mark.Key}' goes after {DescribeChased(mark.Target)}, as the scene host's does");
            return;
        }

        if (FindChaser(mark, out var kind, out var fsm) && kind.StepInto(update.Responder) is { } step &&
            Array.IndexOf(step.TakenFromStateNames, fsm.ActiveStateName) >= 0) {
            fsm.SetState(update.Responder);
            Logger.Info(
                $"[Thrown] '{mark.Key}' goes into '{update.Responder}' after {DescribeChased(mark.Target)}, as the " +
                "scene host's did"
            );
            return;
        }

        Logger.Info(
            $"[Thrown] '{mark.Key}' stays in '{(FindChaser(mark, out _, out var at) ? at.ActiveStateName : "-")}' " +
            $"rather than go into '{update.Responder}' as the scene host's did, as it does not go there from there"
        );
    }

    /// <summary>
    /// Takes the words of the scene host's that waited for their thing to be here (see <see cref="ApplyThrownChase"/>),
    /// in the order they came, every frame while there are any.
    /// </summary>
    private void RetryEarlyChases() {
        if (_earlyChases.Count == 0) {
            return;
        }

        var waiting = _earlyChases.ToArray();
        _earlyChases.Clear();
        foreach (var (update, since) in waiting) {
            ApplyThrownChase(update, since);
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
    /// <param name="steps">The steps that only the scene host's game takes.</param>
    private sealed class ChaserKind(
        string prefabName,
        string fsmName,
        string[] startStateNames,
        string askStateName,
        ChaserStep[] steps
    ) {
        public readonly string PrefabName = prefabName;
        public readonly string FsmName = fsmName;
        public readonly string[] StartStateNames = startStateNames;
        public readonly string AskStateName = askStateName;

        /// <summary>
        /// The step from one state to another that only the scene host's game takes, or null if it is none.
        /// </summary>
        public ChaserStep? StepOf(string from, string to) {
            return Array.Find(steps, step => step.From == from && step.To == to);
        }

        /// <summary>
        /// The step into a state that only the scene host's game takes, or null if it is none.
        /// </summary>
        public ChaserStep? StepInto(string to) {
            return Array.Find(steps, step => step.To == to);
        }
    }

    /// <summary>
    /// A step of a thing that chases that only the scene host's game takes: it rushes, or, circling, notices a player.
    /// </summary>
    /// <param name="from">The state the step is taken from in the host's game.</param>
    /// <param name="to">The state it goes into.</param>
    /// <param name="takenFromStateNames">The states from which the other games take it on the host's word: a little
    /// behind the host's, their own thing may be a state short of it.</param>
    private sealed class ChaserStep(string from, string to, string[] takenFromStateNames) {
        public readonly string From = from;
        public readonly string To = to;
        public readonly string[] TakenFromStateNames = takenFromStateNames;
    }
}
