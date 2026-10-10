using System.Diagnostics;
using System.Text;

namespace ProtocolCheck.Explore;

/// <summary>
/// The events that can happen next in a state, as the model lists them.
/// </summary>
public sealed class Moves<T> where T : Rec {
    internal readonly List<(string Label, Action<T> Change, bool Ordinary)> Items = [];

    /// <summary>
    /// An ordinary event: the games running, a message arriving, a timer, a player doing what the flow asks of them.
    /// </summary>
    public void Add(string label, Action<T> change) => Items.Add((label, change, true));

    /// <summary>
    /// A disruption: quitting, dropping out, a stretch of the network lost. It may lead into a dead end, but may not be
    /// needed to get out of one, because a player has no way of knowing that it would help.
    /// </summary>
    public void Disrupt(string label, Action<T> change) => Items.Add((label, change, false));
}

/// <summary>
/// A model of a flow: two games and what travels between them, the events that can happen in each state, and what the
/// flow is for.
/// </summary>
public abstract class Model<T> where T : Rec {
    public abstract string Name { get; }

    /// <summary>
    /// What the model leaves out or assumes, printed with its findings.
    /// </summary>
    public virtual string Scope => "";

    public abstract T Initial();

    public abstract void Moves(T state, Moves<T> moves);

    /// <summary>
    /// Whether the flow reached what it is for.
    /// </summary>
    public abstract bool Goal(T state);

    /// <summary>
    /// What is wrong in a state that must never happen, or null.
    /// </summary>
    public virtual string? Invariant(T state) => null;

    /// <summary>
    /// A short description of a state in a dead end, which groups the dead ends.
    /// </summary>
    public virtual string Describe(T state) => "stuck";

    /// <summary>
    /// Changes a new state into the one form that stands for every state that behaves the same, like numbering keys
    /// that only ever get compared from 1 up, so that the search visits them once.
    /// </summary>
    public virtual void Normalize(T state) {
    }
}

public sealed record Finding(string Kind, string Text, IReadOnlyList<string> Trace);

public sealed class Result {
    public required string Model { get; init; }
    public string Scope { get; init; } = "";
    public int States { get; set; }
    public long Events { get; set; }
    public int Goals { get; set; }
    public int DeadEnds { get; set; }
    public bool Complete { get; set; } = true;
    public TimeSpan Time { get; set; }
    public List<Finding> Findings { get; } = [];
}

/// <summary>
/// Visits every state that a model can reach, in every order of events, and reports what goes wrong:
/// <list type="bullet">
/// <item>a broken invariant: a state that the model says must never happen;</item>
/// <item>a dead end: a state from which the goal can no longer be reached by ordinary events alone. Disruptions may
/// have led there, but getting out may not need another one;</item>
/// <item>a stop: a dead end in which nothing can happen at all.</item>
/// </list>
/// Each finding comes with the shortest sequence of events that leads to it, and dead ends are grouped by the model's
/// description of them, so one run shows every different way the flow gets stuck.
/// </summary>
public static class Search {
    public static Result Run<T>(Model<T> model, int limit, Action<string>? progress = null) where T : Rec {
        var clock = Stopwatch.StartNew();
        var result = new Result { Model = model.Name, Scope = model.Scope };

        var labels = new List<string>();
        var labelIds = new Dictionary<string, int>();
        var index = new Dictionary<UInt128, int>();
        var parent = new List<int>();
        var via = new List<int>();
        var goal = new List<bool>();
        var hasMoves = new List<bool>();
        var kinds = new List<string>();
        var kindIds = new Dictionary<string, int>();
        var kindOf = new List<int>();
        var ordinaryFrom = new List<int>();
        var ordinaryTo = new List<int>();
        var broken = new Dictionary<string, int>();
        var expanded = new List<bool>();
        var hasher = new Hasher();

        (int Id, bool New) Add(T state, int from, int label) {
            hasher.Reset();
            Walker.Walk(state, hasher);
            var key = hasher.Result;
            if (index.TryGetValue(key, out var found)) {
                return (found, false);
            }

            var id = parent.Count;
            index[key] = id;
            expanded.Add(false);
            parent.Add(from);
            via.Add(label);
            goal.Add(model.Goal(state));
            hasMoves.Add(false);
            var description = model.Describe(state);
            if (!kindIds.TryGetValue(description, out var kind)) {
                kind = kindIds[description] = kinds.Count;
                kinds.Add(description);
            }

            kindOf.Add(kind);
            if (model.Invariant(state) is { } problem) {
                broken.TryAdd(problem, id);
            }

            return (id, true);
        }

        var queue = new Queue<(int Id, T State)>();
        var first = model.Initial();
        Add(first, -1, -1);
        queue.Enqueue((0, first));
        var moves = new Moves<T>();
        var seen = new HashSet<string>();
        var nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);

        while (queue.Count > 0) {
            var (id, state) = queue.Dequeue();
            expanded[id] = true;
            moves.Items.Clear();
            model.Moves(state, moves);
            seen.Clear();
            foreach (var (label, change, ordinary) in moves.Items) {
                if (!seen.Add(label)) {
                    throw new InvalidOperationException($"Two events are both called '{label}' in one state");
                }

                var next = state.Clone<T>();
                change(next);
                model.Normalize(next);
                hasMoves[id] = true;
                if (!labelIds.TryGetValue(label, out var labelId)) {
                    labelId = labelIds[label] = labels.Count;
                    labels.Add(label);
                }

                var (nextId, isNew) = Add(next, id, labelId);
                result.Events++;
                if (ordinary) {
                    ordinaryFrom.Add(id);
                    ordinaryTo.Add(nextId);
                }

                if (isNew) {
                    queue.Enqueue((nextId, next));
                }
            }

            if (parent.Count >= limit) {
                result.Complete = false;
                break;
            }

            if (progress != null && clock.Elapsed >= nextReport) {
                nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);
                progress($"{model.Name}: {parent.Count} states, {queue.Count} waiting");
            }
        }

        var count = parent.Count;
        result.States = count;
        result.Goals = goal.Count(reached => reached);

        // Which states can still reach the goal by ordinary events alone, going backwards from every goal state
        var starts = new int[count + 1];
        foreach (var target in ordinaryTo) {
            starts[target + 1]++;
        }

        for (var i = 0; i < count; i++) {
            starts[i + 1] += starts[i];
        }

        var sources = new int[ordinaryTo.Count];
        var fill = (int[]) starts.Clone();
        for (var k = 0; k < ordinaryTo.Count; k++) {
            sources[fill[ordinaryTo[k]]++] = ordinaryFrom[k];
        }

        // A state the search never got to look at, because it stopped at the limit, might still get there. Counting
        // it as able to keeps what is reported a real dead end: one that can't get out even through those
        var canFinish = new bool[count];
        var back = new Queue<int>();
        for (var i = 0; i < count; i++) {
            if (goal[i] || !expanded[i]) {
                canFinish[i] = true;
                back.Enqueue(i);
            }
        }

        while (back.Count > 0) {
            var target = back.Dequeue();
            for (var k = starts[target]; k < starts[target + 1]; k++) {
                var source = sources[k];
                if (!canFinish[source]) {
                    canFinish[source] = true;
                    back.Enqueue(source);
                }
            }
        }

        List<string> Trace(int id) {
            var steps = new List<string>();
            while (id > 0) {
                steps.Add(labels[via[id]]);
                id = parent[id];
            }

            steps.Reverse();
            return steps;
        }

        foreach (var (problem, id) in broken) {
            result.Findings.Add(new Finding("invariant", problem, Trace(id)));
        }

        // A dead end is shown where it comes to rest: a state in which no ordinary event changes anything any more.
        // Grouped by what that state looks like, which is what a player would see, rather than by the first state
        // that could no longer get out, which is usually one where nothing has gone visibly wrong yet
        var changes = new bool[count];
        for (var k = 0; k < ordinaryFrom.Count; k++) {
            if (ordinaryFrom[k] != ordinaryTo[k]) {
                changes[ordinaryFrom[k]] = true;
            }
        }

        var resting = new Dictionary<string, int>();
        var restless = new Dictionary<string, int>();
        for (var i = 0; i < count; i++) {
            if (canFinish[i]) {
                continue;
            }

            result.DeadEnds++;
            if (!changes[i]) {
                resting.TryAdd(kinds[kindOf[i]], i);
            } else {
                restless.TryAdd(kinds[kindOf[i]], i);
            }
        }

        // Dead ends that never come to rest go round for ever, like a question asked again and again that is never
        // answered, and are only shown when no dead end comes to rest
        var shown = resting.Count > 0 ? resting : restless;
        foreach (var (description, id) in shown.OrderBy(pair => pair.Value)) {
            var kind = resting.Count > 0 ? hasMoves[id] ? "dead end" : "stop" : "dead end, going round";

            result.Findings.Add(new Finding(kind, description, Trace(id)));
        }

        result.Time = clock.Elapsed;
        return result;
    }

    /// <summary>
    /// Runs a model by random walks rather than through every order, for models too large for that, like three players
    /// with something going wrong: each walk starts at the first state and takes events picked at random, any of them
    /// at each step, for a number of steps, checking the invariant at every state. From where a walk ends, ordinary
    /// events picked at random go on until the goal is reached, tried a few times over; a walk from whose end the goal
    /// was never reached that way is reported as a likely dead end, and one that ends where nothing can happen as a
    /// stop. It proves nothing, but it reaches orders far deeper than the full search gets to.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="walks">How many walks.</param>
    /// <param name="steps">How many events each walk takes, and how many each try to reach the goal may take.</param>
    /// <param name="seed">The seed of the first walk; each walk after it takes the next one, so a finding comes back
    /// with the same seed.</param>
    /// <param name="progress">Where to say how far it has got.</param>
    public static Result Walk<T>(Model<T> model, int walks, int steps, int seed, Action<string>? progress = null)
        where T : Rec {
        const int tries = 3;
        var clock = Stopwatch.StartNew();
        var result = new Result { Model = $"{model.Name}, {walks} random walks", Scope = model.Scope };
        var shortest = new Dictionary<(string Kind, string Text), List<string>>();
        var moves = new Moves<T>();
        var nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);

        void Note(string kind, string text, List<string> trace) {
            if (!shortest.TryGetValue((kind, text), out var known) || trace.Count < known.Count) {
                shortest[(kind, text)] = new List<string>(trace);
            }
        }

        // Takes one event picked at random, ordinary ones only if asked; false when there is none to take or the
        // state it led to is broken
        bool Step(Random random, ref T state, List<string> trace, bool ordinaryOnly) {
            moves.Items.Clear();
            model.Moves(state, moves);
            var count = 0;
            foreach (var item in moves.Items) {
                if (!ordinaryOnly || item.Ordinary) {
                    count++;
                }
            }

            if (count == 0) {
                return false;
            }

            var pick = random.Next(count);
            foreach (var (label, change, ordinary) in moves.Items) {
                if (ordinaryOnly && !ordinary || pick-- > 0) {
                    continue;
                }

                var next = state.Clone<T>();
                change(next);
                model.Normalize(next);
                state = next;
                trace.Add(label);
                result.Events++;
                break;
            }

            if (model.Invariant(state) is { } problem) {
                Note("invariant", problem, trace);
                return false;
            }

            return true;
        }

        for (var walk = 0; walk < walks; walk++) {
            var random = new Random(seed + walk);
            var state = model.Initial();
            var trace = new List<string>();
            var broken = false;
            for (var step = 0; step < steps; step++) {
                if (!Step(random, ref state, trace, false)) {
                    broken = model.Invariant(state) != null;
                    break;
                }

                if (model.Goal(state)) {
                    result.Goals++;
                }
            }

            result.States++;
            if (broken) {
                continue;
            }

            // From where it ended, the goal again by ordinary events alone
            var reached = model.Goal(state);
            for (var attempt = 0; attempt < tries && !reached; attempt++) {
                var tryState = state;
                var tryTrace = new List<string>(trace);
                for (var step = 0; step < steps && !reached; step++) {
                    if (!Step(random, ref tryState, tryTrace, true)) {
                        break;
                    }

                    reached = model.Goal(tryState);
                }
            }

            if (!reached) {
                result.DeadEnds++;
                moves.Items.Clear();
                model.Moves(state, moves);
                Note(moves.Items.Count == 0 ? "stop" : "likely dead end", model.Describe(state), trace);
            }

            if (progress != null && clock.Elapsed >= nextReport) {
                nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);
                progress($"{model.Name}: {walk + 1} of {walks} walks");
            }
        }

        foreach (var ((kind, text), trace) in shortest.OrderBy(pair => pair.Value.Count)) {
            result.Findings.Add(new Finding(kind, text, trace));
        }

        result.Complete = false;
        result.Time = clock.Elapsed;
        return result;
    }

    /// <summary>
    /// The states along a sequence of events, starting with the first state.
    /// </summary>
    public static List<T> Replay<T>(Model<T> model, IReadOnlyList<string> steps) where T : Rec {
        var state = model.Initial();
        var states = new List<T> { state };
        var moves = new Moves<T>();
        foreach (var step in steps) {
            moves.Items.Clear();
            model.Moves(state, moves);
            var move = moves.Items.FirstOrDefault(item => item.Label == step);
            if (move.Change == null) {
                throw new InvalidOperationException($"No event '{step}' in this state");
            }

            state = state.Clone<T>();
            move.Change(state);
            model.Normalize(state);
            states.Add(state);
        }

        return states;
    }

    /// <summary>
    /// A sequence of events with what each one changed.
    /// </summary>
    public static string FormatTrace<T>(Model<T> model, IReadOnlyList<string> steps) where T : Rec {
        var states = Replay(model, steps);
        var builder = new StringBuilder();
        var before = Fields.Flatten(states[0]).ToList();
        builder.Append("      start: ").AppendLine(string.Join(", ", before.Select(pair => $"{pair.Name}={pair.Value}")));
        for (var i = 0; i < steps.Count; i++) {
            var after = Fields.Flatten(states[i + 1]).ToList();
            var old = before.ToDictionary(pair => pair.Name, pair => pair.Value);
            var changed = after.Where(pair => !old.TryGetValue(pair.Name, out var value) || value != pair.Value)
                .Select(pair => $"{pair.Name}={pair.Value}")
                .ToList();
            builder.Append($"      {i + 1,3}. ").AppendLine(steps[i]);
            if (changed.Count > 0) {
                builder.Append("           ").AppendLine(string.Join(", ", changed));
            }

            before = after;
        }

        return builder.ToString();
    }

    public static void Report<T>(Model<T> model, Result result, TextWriter output, bool traces, bool walked = false)
        where T : Rec {
        var status = result.Complete ? "" : " (cut short at the state limit)";
        output.WriteLine(walked
            ? $"{result.Model}: {result.Events} events, {result.Goals} states at the goal on the way, " +
              $"{result.DeadEnds} walks that ended out of reach of it, {result.Time.TotalSeconds:F1}s"
            : $"{result.Model}: {result.States} states, {result.Events} events, {result.Goals} at the goal, " +
              $"{result.DeadEnds} in dead ends, {result.Time.TotalSeconds:F1}s{status}"
        );
        if (result.Scope.Length > 0) {
            output.WriteLine($"  scope: {result.Scope}");
        }

        foreach (var finding in result.Findings) {
            output.WriteLine($"  [{finding.Kind}] {finding.Text} ({finding.Trace.Count} events)");
            if (traces) {
                output.Write(FormatTrace(model, finding.Trace));
            }
        }

        if (result.Findings.Count == 0) {
            output.WriteLine("  nothing found");
        }
    }
}
