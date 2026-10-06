using System;
using System.Diagnostics;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// An event that keeps the time that each of its handlers takes, for <see cref="FrameWatch"/>. It runs its handlers in
/// the order they were added, like an ordinary event does. An exception of one of them ends only that one's run: it is
/// written to the log, and the handlers after it run as ever.
///
/// Like an ordinary event, it used to go out of the event and leave the handlers after it to the next run. A handler
/// that threw every frame then left them for good: one left behind by a room, failing on that room's particles once
/// they were gone, kept every creature's copy in every room after from moving for the rest of the session. Nothing of
/// it was in the log either, where a player's game cannot write Unity's own messages.
/// </summary>
internal sealed class TimedEvent {
    /// <summary>
    /// A handler of the event, with the time it took.
    /// </summary>
    internal sealed class Handler(Action action, string name) : TimedPart(name) {
        /// <summary>
        /// What the handler does.
        /// </summary>
        public readonly Action Action = action;

        /// <summary>
        /// How many times it threw.
        /// </summary>
        private int _failures;

        /// <summary>
        /// Writes down that the handler threw: the first time with all of it, and after that only how often, at every
        /// tenfold, since a handler that throws once usually throws every frame.
        /// </summary>
        public void SayItFailed(Exception exception) {
            _failures++;
            if (_failures == 1) {
                Logger.Error($"'{Name}', which runs every frame, failed; what runs after it still does:\n{exception}");
            } else if (_failures is 10 or 100 or 1000 or 10000 or 100000) {
                Logger.Error(
                    $"'{Name}' has failed {_failures} times now: {exception.GetType().Name}: {exception.Message}"
                );
            }
        }
    }

    /// <summary>
    /// What adding and removing handlers take turns on, as an ordinary event can be added to from any thread.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// The handlers of the event, in the order they run. Adding or removing one makes a new array, so that a run of
    /// the event goes through the handlers that were there when it began.
    /// </summary>
    public Handler[] Handlers { get; private set; } = [];

    /// <summary>
    /// The time all handlers of the event took in this frame, in ticks of <see cref="Stopwatch"/>.
    /// </summary>
    public long FrameTicks { get; private set; }

    /// <summary>
    /// How many times the event ran in this frame.
    /// </summary>
    public int FrameRuns { get; private set; }

    /// <summary>
    /// Adds a handler at the end of the event.
    /// </summary>
    public void Add(Action? action) {
        if (action == null) {
            return;
        }

        lock (_lock) {
            var handlers = new Handler[Handlers.Length + 1];
            Array.Copy(Handlers, handlers, Handlers.Length);
            handlers[^1] = new Handler(action, Describe(action));
            Handlers = handlers;
        }
    }

    /// <summary>
    /// Removes the last handler of the event that does the same as the given one, like an ordinary event does.
    /// </summary>
    public void Remove(Action? action) {
        if (action == null) {
            return;
        }

        lock (_lock) {
            var index = Array.FindLastIndex(Handlers, handler => handler.Action.Equals(action));
            if (index < 0) {
                return;
            }

            var handlers = new Handler[Handlers.Length - 1];
            Array.Copy(Handlers, handlers, index);
            Array.Copy(Handlers, index + 1, handlers, index, handlers.Length - index);
            Handlers = handlers;
        }
    }

    /// <summary>
    /// Runs the handlers of the event, keeping the time each of them takes.
    /// </summary>
    public void Invoke() {
        FrameRuns++;

        var handlers = Handlers;
        var start = Stopwatch.GetTimestamp();
        var last = start;
        try {
            foreach (var handler in handlers) {
                FrameWatch.Running = handler.Name;
                try {
                    handler.Action();
                } catch (Exception e) {
                    handler.SayItFailed(e);
                }

                var now = Stopwatch.GetTimestamp();
                handler.FrameTicks += now - last;
                last = now;
            }
        } finally {
            FrameWatch.Running = null;
            FrameTicks += last - start;
        }
    }

    /// <summary>
    /// Ends the frame: what the handlers took in it is added to the frames counted together, if it counts, and the
    /// next frame starts from nothing.
    /// </summary>
    public void EndFrame(bool counts) {
        foreach (var handler in Handlers) {
            handler.EndFrame(counts);
        }

        FrameTicks = 0;
        FrameRuns = 0;
    }

    /// <summary>
    /// Forgets what the handlers took in the frames counted together, which start again from nothing.
    /// </summary>
    public void EndStretch() {
        foreach (var handler in Handlers) {
            handler.StretchTicks = 0;
        }
    }

    /// <summary>
    /// Names a handler by its class and method. A lambda is named by the method it was written in, as the compiler
    /// puts it in a class of its own inside the class it was written in.
    /// </summary>
    public static string Describe(Delegate action) {
        var method = action.Method;
        var type = method.DeclaringType;
        while (type is { DeclaringType: not null } && type.Name.StartsWith("<", StringComparison.Ordinal)) {
            type = type.DeclaringType;
        }

        var name = method.Name;
        var end = name.IndexOf('>');
        if (name.StartsWith("<", StringComparison.Ordinal) && end > 1) {
            name = $"{name.Substring(1, end - 1)} (lambda)";
        }

        return $"{type?.Name}.{name}";
    }
}

/// <summary>
/// A part of the mod's work of each frame, with the time it took, for <see cref="FrameWatch"/>.
/// </summary>
internal class TimedPart(string name) {
    /// <summary>
    /// The class and method that does the part, for the log.
    /// </summary>
    public readonly string Name = name;

    /// <summary>
    /// The time the part took in this frame, in ticks of <see cref="Stopwatch"/>.
    /// </summary>
    public long FrameTicks;

    /// <summary>
    /// The time the part took in the frames that <see cref="FrameWatch"/> counts together now, in ticks of
    /// <see cref="Stopwatch"/>.
    /// </summary>
    public long StretchTicks;

    /// <summary>
    /// Ends the frame: what the part took in it is added to the frames counted together, if it counts, and the next
    /// frame starts from nothing.
    /// </summary>
    public void EndFrame(bool counts) {
        if (counts) {
            StretchTicks += FrameTicks;
        }

        FrameTicks = 0;
    }
}
