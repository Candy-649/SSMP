using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using Math_Vector2 = SSMP.Math.Vector2;
using Math_Vector3 = SSMP.Math.Vector3;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What of the variables of the FSMs of a creature that this game runs goes to the other game, and when.
///
/// The FSM of a boss works out many numbers every frame for itself: how high it is and how fast it falls, how far the
/// player is, where its thrown needle has got to, how much of its stun is left. It works them out to know when to go
/// on to its next move, and going on is what the other game is told, once. Each of those numbers still went to the
/// other game every frame it changed, by the way that is sent again until it arrives, while nothing there reads them in
/// between: the copy only follows (USER 11-03, "像织针这样本来就可以只发一次的，代码里还有没有每帧都发的"; "这个数就是为了换
/// 招，而换招本身已经从房主那边传过来了所以不需要这个数").
///
/// So the numbers and points of an FSM go as it goes into a state and ahead of each replay of an action of the state:
/// all of them that changed, and what the rest of going into the state sets with the next look at the FSMs. In between
/// only those go that the other game reads while the FSM stays in the state:
/// - those of the actions of the state whose replays went there and that still run here, which the other game runs
///   there for as long as they run (EntityFsmActions.RunInState), like a part that a boss puts where a number says
///   every frame;
/// - those that the copy reads as it answers a wound of its player by itself from the state (CoopHits.SetOffByWound):
///   a few small creatures count the time to their next attack in a number that the answer looks at.
/// Nothing else that a copy plays by itself reads a number that the state it starts from works out every frame - not a
/// catch, a grab or a hook, nor what the things do that play each strike in the striker's game first - and the FSMs of
/// the room read none of the numbers of a creature every frame, only some of its yes-or-no's (FsmScan, 11-03). Its
/// yes-or-no's, whole numbers and words still go whenever they change, which is seldom.
///
/// What the other game holds of a state that has gone on for a while is then what the creature held as it went into
/// it. A copy that plays a move by itself works out what the move needs as it goes. A game that takes a creature over
/// starts its waits in that state again, as it always did, and now a count kept in a number as well.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// How many states, from the one that a wound leads to on, are looked through for what the copy reads as it
    /// answers the wound (see <see cref="WoundReadsOf"/>).
    /// </summary>
    private const int WoundAnswerStates = 5;

    /// <summary>
    /// Notes that the replay of an action of an FSM of the creature went to the other game, which runs it there for as
    /// long as it runs here (see <see cref="AddVariableChanges"/>).
    /// </summary>
    /// <param name="hooked">The action.</param>
    private void NoteReplayed(HookedEntityAction hooked) {
        var replayed = _fsmSnapshots[hooked.FsmIndex].Replayed;

        // What went of a state that the FSM has left, the other game runs no longer
        if (replayed.Count > 0 && replayed[0].Action.State != hooked.Action.State) {
            replayed.Clear();
        }

        if (!replayed.Contains(hooked)) {
            replayed.Add(hooked);
        }

        hooked.Reads ??= FindWhatTheyRead(new[] { hooked.Action }, _fsms.Host[hooked.FsmIndex].FsmVariables);
    }

    /// <summary>
    /// The numbers and points that the copy of the creature reads as it answers a wound of its player by itself from a
    /// state of an FSM (Entity.FindWoundReactions): those of the state that the wound leads to and of the states that
    /// follow it as each finishes, found the first time the FSM is in the state. A wound that leads into a state that
    /// counts it the copy does not answer by itself (HitCounters).
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="snapshot">What last went of it, which keeps what was found.</param>
    /// <param name="state">The state.</param>
    private static NumbersRead WoundReadsOf(PlayMakerFSM fsm, FsmSnapshot snapshot, FsmState state) {
        if (snapshot.ReadByWounds.TryGetValue(state, out var reads)) {
            return reads;
        }

        var actions = new List<FsmStateAction>();
        foreach (var eventName in WoundEvents) {
            if (EntityFsmActions.FindTransition(fsm.Fsm, state, eventName) is not { } next) {
                continue;
            }

            if (!HitCounters.Counts(next)) {
                var path = new List<FsmState>();
                for (FsmState? at = next;
                     at != null && at != state && !path.Contains(at) && path.Count < WoundAnswerStates;
                     at = EntityFsmActions.FindTransition(fsm.Fsm, at, "FINISHED")) {
                    path.Add(at);
                    foreach (var action in at.Actions) {
                        if (action.Enabled) {
                            actions.Add(action);
                        }
                    }
                }
            }

            break;
        }

        reads = FindWhatTheyRead(actions, fsm.FsmVariables);
        snapshot.ReadByWounds[state] = reads;
        return reads;
    }

    /// <summary>
    /// Finds the float, vector2 and vector3 variables of their FSM that actions are given their numbers and points by:
    /// the ones that the copy of a part that an action moves takes from the scene host too (see
    /// <see cref="EntityFsmActions.ValueFieldsOf"/>).
    /// </summary>
    /// <param name="actions">The actions.</param>
    /// <param name="variables">The variables of their FSM.</param>
    private static NumbersRead FindWhatTheyRead(IReadOnlyList<FsmStateAction> actions, FsmVariables variables) {
        var floats = new List<int>();
        var vector2s = new List<int>();
        var vector3s = new List<int>();
        foreach (var action in actions) {
            foreach (var field in EntityFsmActions.ValueFieldsOf(action.GetType())) {
                switch (field.GetValue(action)) {
                    case FsmFloat number:
                        NoteIndex(floats, variables.FloatVariables, number);
                        break;
                    case FsmVector2 point:
                        NoteIndex(vector2s, variables.Vector2Variables, point);
                        break;
                    case FsmVector3 point:
                        NoteIndex(vector3s, variables.Vector3Variables, point);
                        break;
                }
            }
        }

        return new NumbersRead {
            Floats = floats.ToArray(),
            Vector2s = vector2s.ToArray(),
            Vector3s = vector3s.ToArray()
        };
    }

    /// <summary>
    /// Notes where among the variables of an FSM the one is that a field of an action holds, if it holds one of them:
    /// a number set in the FSM itself has no name (see EntityFsmActions.ReadValues), and one of the whole game is not
    /// among them.
    /// </summary>
    /// <param name="found">Where the variables found so far are.</param>
    /// <param name="variables">The variables of the FSM of that kind.</param>
    /// <param name="held">What the field holds.</param>
    private static void NoteIndex(List<int> found, NamedVariable[] variables, NamedVariable held) {
        if (string.IsNullOrEmpty(held.Name)) {
            return;
        }

        for (var i = 0; i < variables.Length; i++) {
            if (variables[i].Name == held.Name) {
                if (!found.Contains(i)) {
                    found.Add(i);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Whether the other game still runs an action of an FSM whose replay went there: an action of the state the FSM is
    /// in that has not finished here.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="hooked">The action.</param>
    private static bool StillRunsThere(PlayMakerFSM fsm, HookedEntityAction hooked) {
        return hooked.Action.State == fsm.Fsm.ActiveState && !hooked.Action.Finished;
    }

    /// <summary>
    /// Adds to what goes of an FSM of the creature the values of its variables that changed since they last went and
    /// go now, which it notes as gone: all of them, as the FSM goes into a state or an action of it is replayed, or
    /// else its yes-or-no's, whole numbers and words and only those numbers and points that the other game reads while
    /// the FSM stays in its state (see the summary of this part of the class). Nothing is taken from the pool unless
    /// something goes: this looks at every FSM of every creature every frame.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="snapshot">What last went of it.</param>
    /// <param name="all">Whether all of its variables go.</param>
    /// <param name="data">What goes of it now, or null if nothing does yet.</param>
    /// <returns>What goes of it now, or null if nothing does.</returns>
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private static EntityHostFsmData? AddVariableChanges(
        PlayMakerFSM fsm,
        FsmSnapshot snapshot,
        bool all,
        EntityHostFsmData? data
    ) {
        var variables = fsm.FsmVariables;
        if (all) {
            for (var i = 0; i < variables.FloatVariables.Length; i++) {
                AddFloat(i);
            }

            for (var i = 0; i < variables.Vector2Variables.Length; i++) {
                AddVector2(i);
            }

            for (var i = 0; i < variables.Vector3Variables.Length; i++) {
                AddVector3(i);
            }
        } else {
            foreach (var hooked in snapshot.Replayed) {
                if (StillRunsThere(fsm, hooked)) {
                    AddRead(hooked.Reads!);
                }
            }

            if (fsm.Fsm.ActiveState is { } state) {
                AddRead(WoundReadsOf(fsm, snapshot, state));
            }
        }

        for (var i = 0; i < variables.IntVariables.Length; i++) {
            var value = variables.IntVariables[i].Value;
            if (value != snapshot.Ints[i]) {
                snapshot.Ints[i] = value;
                Changed(EntityHostFsmData.Type.Ints).Ints[(byte) i] = value;
            }
        }

        for (var i = 0; i < variables.BoolVariables.Length; i++) {
            var value = variables.BoolVariables[i].Value;
            if (value != snapshot.Bools[i]) {
                snapshot.Bools[i] = value;
                Changed(EntityHostFsmData.Type.Bools).Bools[(byte) i] = value;
            }
        }

        for (var i = 0; i < variables.StringVariables.Length; i++) {
            var value = variables.StringVariables[i].Value;
            if (snapshot.Strings[i] != null && value != snapshot.Strings[i]) {
                snapshot.Strings[i] = value;
                Changed(EntityHostFsmData.Type.Strings).Strings[(byte) i] = value;
            }
        }

        return data;

        void AddRead(NumbersRead reads) {
            foreach (var i in reads.Floats) {
                AddFloat(i);
            }

            foreach (var i in reads.Vector2s) {
                AddVector2(i);
            }

            foreach (var i in reads.Vector3s) {
                AddVector3(i);
            }
        }

        void AddFloat(int i) {
            var value = variables.FloatVariables[i].Value;
            if (value != snapshot.Floats[i]) {
                snapshot.Floats[i] = value;
                Changed(EntityHostFsmData.Type.Floats).Floats[(byte) i] = value;
            }
        }

        void AddVector2(int i) {
            var value = variables.Vector2Variables[i].Value;
            if (value != snapshot.Vector2s[i]) {
                snapshot.Vector2s[i] = value;
                Changed(EntityHostFsmData.Type.Vector2s).Vec2s[(byte) i] = (Math_Vector2) value;
            }
        }

        void AddVector3(int i) {
            var value = variables.Vector3Variables[i].Value;
            if (value != snapshot.Vector3s[i]) {
                snapshot.Vector3s[i] = value;
                Changed(EntityHostFsmData.Type.Vector3s).Vec3s[(byte) i] = (Math_Vector3) value;
            }
        }

        EntityHostFsmData Changed(EntityHostFsmData.Type type) {
            data ??= ObjectPool<EntityHostFsmData>.Get();
            data.Types.Add(type);
            return data;
        }
    }
}
