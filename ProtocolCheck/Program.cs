using ProtocolCheck.Explore;
using ProtocolCheck.Models;

namespace ProtocolCheck;

/// <summary>
/// Runs models of SSMP's co-op flows through every order of events and prints what goes wrong.
/// <code>
/// dotnet run -c Release --project ProtocolCheck                    every model of the code as it is
/// dotnet run -c Release --project ProtocolCheck -- save            the models whose name has "save" in it
/// dotnet run -c Release --project ProtocolCheck -- --no-traces     only the summaries
/// dotnet run -c Release --project ProtocolCheck -- --limit 500000  at most this many states per model
/// dotnet run -c Release --project ProtocolCheck -- --deep          also the models too large to finish
/// dotnet run -c Release --project ProtocolCheck -- --before-fixes  also the code before its fixes, to see the finds
/// dotnet run -c Release --project ProtocolCheck -- --walk 20000    random walks instead, also of the models too
///                                                                 large to finish (--steps 400, --seed 1 by default)
/// </code>
/// </summary>
internal static class Program {
    private static int Main(string[] args) {
        var traces = !args.Contains("--no-traces");
        var deep = args.Contains("--deep");
        var beforeFixes = args.Contains("--before-fixes");
        var limit = 5_000_000;
        var walks = 0;
        var steps = 400;
        var seed = 1;
        var names = new List<string>();
        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--limit" && i + 1 < args.Length) {
                limit = int.Parse(args[++i]);
            } else if (args[i] == "--walk" && i + 1 < args.Length) {
                walks = int.Parse(args[++i]);
            } else if (args[i] == "--steps" && i + 1 < args.Length) {
                steps = int.Parse(args[++i]);
            } else if (args[i] == "--seed" && i + 1 < args.Length) {
                seed = int.Parse(args[++i]);
            } else if (!args[i].StartsWith("--")) {
                names.Add(args[i]);
            }
        }

        var found = 0;
        foreach (var run in Runs(deep || walks > 0, beforeFixes)) {
            if (names.Count > 0 && !names.Any(name => run.Name.Contains(name, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            found += walks > 0 ? run.Walk(walks, steps, seed, traces) : run.Execute(limit, traces);
            Console.WriteLine();
        }

        return found > 0 ? 1 : 0;
    }

    private static IEnumerable<IRun> Runs(bool deep, bool beforeFixes) {
        yield return new Run<SaveCheck.World>(new SaveCheck(2, 0));
        yield return new Run<SaveCheck.World>(new SaveCheck(2, 1));
        // Three players reach far more orders, so their world progress goes in one part and time passes only once
        // every message arrived
        yield return new Run<SaveCheck.World>(new SaveCheck(3, 0, 0, 1));
        yield return new Run<SaveCheck.World>(new SaveCheck(3, 1, 0, 1));
        if (deep) {
            // Over 12 million states and not done; a run cut short only reports what it could finish. Three players
            // with time passing while two messages are on their way passed 20 million states without an end.
            yield return new Run<SaveCheck.World>(new SaveCheck(2, 2));
            yield return new Run<SaveCheck.World>(new SaveCheck(3, 0, 2, 1));
            yield return new Run<SaveCheck.World>(new SaveCheck(3, 2));
        }

        yield return new Run<SceneHost.World>(new SceneHost(1));
        if (beforeFixes) {
            yield return new Run<SceneHost.World>(new SceneHost(1, sceneStamped: false));
        }
    }

    private interface IRun {
        string Name { get; }

        int Execute(int limit, bool traces);

        int Walk(int walks, int steps, int seed, bool traces);
    }

    private sealed class Run<T>(Model<T> model) : IRun where T : Rec {
        public string Name => model.Name;

        public int Execute(int limit, bool traces) {
            var result = Search.Run(model, limit, line => Console.Error.WriteLine("  " + line));
            Search.Report(model, result, Console.Out, traces);
            return result.Findings.Count;
        }

        public int Walk(int walks, int steps, int seed, bool traces) {
            var result = Search.Walk(model, walks, steps, seed, line => Console.Error.WriteLine("  " + line));
            Search.Report(model, result, Console.Out, traces, walked: true);
            return result.Findings.Count;
        }
    }
}
