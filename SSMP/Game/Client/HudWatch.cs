using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Writes down each time the display of the player's health and silk is told to slide away or come back, and what told
/// it: a step of a state machine, or the game's own code. Not every way back names the display: the game ends an
/// interaction by force when the player is interrupted, and that brings the display back too
/// (InteractManager.OnHeroInterrupted), which is how it comes back at a roar after an inspection (USER 11-08
/// "我刚刚根本没有血条"). Only written down.
/// </summary>
internal static class HudWatch {
    /// <summary>
    /// The state machine of the display that slides it away and back.
    /// </summary>
    private const string SlideFsmName = "Slide Out";

    /// <summary>
    /// The name of the display's object.
    /// </summary>
    private const string HudName = "Hud Canvas";

    /// <summary>
    /// How many places in the game's code are written down for an event that no state machine sent.
    /// </summary>
    private const int CallerCount = 3;

    /// <summary>
    /// The hook on the way every state machine takes an event.
    /// </summary>
    private static Hook? _hook;

    /// <summary>
    /// Whether writing down failed once, after which it stops.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Hooks the way every state machine takes an event.
    /// </summary>
    public static void Initialize() {
        try {
            var method = typeof(Fsm).GetMethod(
                "ProcessEvent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(FsmEvent), typeof(FsmEventData)],
                null
            );
            _hook = new Hook(
                method,
                new Action<Action<Fsm, FsmEvent?, FsmEventData?>, Fsm, FsmEvent?, FsmEventData?>(OnProcessEvent)
            );
        } catch (Exception e) {
            Logger.Warn($"What slides the health display away and back can't be written down: {e}");
        }
    }

    private static void OnProcessEvent(
        Action<Fsm, FsmEvent?, FsmEventData?> orig,
        Fsm self,
        FsmEvent? fsmEvent,
        FsmEventData? eventData
    ) {
        if (!_failed && fsmEvent?.Name is "IN" or "OUT" or "OUT INSTANT" or "DISABLE" && self.Name == SlideFsmName &&
            self.GameObject != null && self.GameObject.name == HudName) {
            Say(self, fsmEvent.Name);
        }

        orig(self, fsmEvent, eventData);
    }

    /// <summary>
    /// Writes down an event that the display is sent, before it takes it.
    /// </summary>
    private static void Say(Fsm hud, string eventName) {
        try {
            var sender = FsmExecutionStack.ExecutingFsm;
            var from = sender != null
                ? $"'{sender.Name}' of '{(sender.GameObject != null ? sender.GameObject.name : "?")}' in " +
                  $"'{FsmExecutionStack.ExecutingStateName}'"
                : $"the game's code ({CallersOutsidePlayMaker()})";
            Logger.Info($"[HUD] The health display, in '{hud.ActiveStateName}', is sent '{eventName}' by {from}");
        } catch (Exception e) {
            _failed = true;
            Logger.Warn($"Could not write down what the health display was sent: {e}");
        }
    }

    /// <summary>
    /// The first places in the game's code on the way to the event, leaving out PlayMaker's own and this class.
    /// </summary>
    private static string CallersOutsidePlayMaker() {
        var callers = new StringBuilder();
        var count = 0;
        foreach (var frame in new StackTrace(2, false).GetFrames() ?? []) {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (type == null || type == typeof(HudWatch) || type == typeof(PlayMakerFSM) ||
                type.Namespace?.StartsWith("HutongGames.PlayMaker", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DMD", StringComparison.Ordinal) == true) {
                continue;
            }

            if (count > 0) {
                callers.Append(" < ");
            }

            callers.Append(type.FullName).Append('.').Append(method!.Name);
            if (++count == CallerCount) {
                break;
            }
        }

        return count == 0 ? "none found" : callers.ToString();
    }
}
