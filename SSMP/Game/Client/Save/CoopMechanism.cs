using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// A mechanism that the interact button starts, like a toll machine or a door that takes an item. Its FSM waits for the
/// interact event in an idle state, runs a prompt in which the hero pays or confirms, and then changes the world, which
/// two-player saves replay in the game of the partner. FsmScan's --interactions sorts the FSMs of all scenes with the
/// same rules, which is how they were checked.
/// </summary>
internal sealed class CoopMechanism {
    /// <summary>
    /// The event that the interact button sends to the FSM of an interactable object.
    /// </summary>
    private const string InteractEvent = "INTERACT";

    /// <summary>
    /// Events that a state waits for while a dialogue or a yes/no box is open.
    /// </summary>
    private static readonly HashSet<string> WaitEvents = ["YES", "NO", "CONVO_END", "LINE_END"];

    /// <summary>
    /// Events that leave a prompt without paying or confirming.
    /// </summary>
    private static readonly HashSet<string> CancelEvents = ["CANCEL", "NO", "FALSE", "HERO DAMAGED", "CONVO_END_FORCED"];

    /// <summary>
    /// Actions of the part of an interaction that belongs to the hero who pressed the button: prompts, payment,
    /// dialogue and moving the hero.
    /// </summary>
    private static readonly string[] PromptPrefixes = [
        "RunFSM", "DialogueYesNo", "RunDialogue", "TakeCurrency", "CurrencyCounterMethod",
        "RespondToCurrencyCounterEvents", "CollectableItemTake", "AddHeroInputBlocker", "HeroRelinquishControl",
        "WaitForHeroInPosition", "DoHeroMovement", "RelicBoardOwnerYesNo", "QuestCompleteYesNo"
    ];

    /// <summary>
    /// Actions of a prompt that pay for or confirm what comes after it.
    /// </summary>
    private static readonly string[] PaymentPrefixes = ["RunFSM", "DialogueYesNo", "TakeCurrency", "CollectableItemTake"];

    /// <summary>
    /// Actions that give the hero something, which makes an interaction belong to each player.
    /// </summary>
    private static readonly string[] GivePrefixes = [
        "SavedItemGet", "CollectableItemCollect", "AddCurrency", "SpawnPowerUpGetMsg", "SpawnSkillGetMsg",
        "SetShopItemPurchased", "OpenSimpleShopMenu"
    ];

    private CoopMechanism(
        HashSet<string> idleStates,
        HashSet<string> promptStates,
        Dictionary<string, HashSet<string>> worldStarts
    ) {
        IdleStates = idleStates;
        PromptStates = promptStates;
        WorldStarts = worldStarts;
    }

    /// <summary>
    /// The states in which the FSM waits for the interact button.
    /// </summary>
    public HashSet<string> IdleStates { get; }

    /// <summary>
    /// The states of the prompt, in which the hero pays or confirms.
    /// </summary>
    public HashSet<string> PromptStates { get; }

    /// <summary>
    /// The states in which the change to the world starts, each with the states it goes through until the FSM waits
    /// for the interact button again.
    /// </summary>
    public Dictionary<string, HashSet<string>> WorldStarts { get; }

    /// <summary>
    /// Whether an action belongs to the prompt of the hero who pressed the button.
    /// </summary>
    public static bool IsPromptAction(FsmStateAction action) => HasPrefix(action, PromptPrefixes);

    /// <summary>
    /// Finds out whether an FSM is a mechanism, and which of its states are idle, prompt and change the world.
    /// </summary>
    /// <returns>The mechanism, or null if the FSM isn't one.</returns>
    public static CoopMechanism? Analyze(Fsm fsm) {
        var allStates = fsm.States ?? [];
        if (!allStates.Any(state => state != null && GetTransitions(state).Any(t => t.EventName == InteractEvent))) {
            return null;
        }

        var states = new Dictionary<string, FsmState>(StringComparer.Ordinal);
        foreach (var state in allStates) {
            if (state != null && state.Name != null) {
                states.TryAdd(state.Name, state);
            }
        }

        var idle = new HashSet<string>(
            states.Values.Where(state => GetTransitions(state).Any(t => t.EventName == InteractEvent))
                .Select(state => state.Name),
            StringComparer.Ordinal
        );
        if (IsCharacterOrBench(fsm, states.Values)) {
            return null;
        }

        var targets = states.Values.Where(state => idle.Contains(state.Name))
            .SelectMany(GetTransitions)
            .Where(t => t.EventName == InteractEvent && t.ToState != null && states.ContainsKey(t.ToState) &&
                        !idle.Contains(t.ToState))
            .Select(t => t.ToState)
            .Distinct()
            .ToList();

        // The prompt: the states from the interaction on that belong to the hero. The world starts in the states that
        // the prompt leads to when the hero paid or confirmed
        var prompt = new HashSet<string>(StringComparer.Ordinal);
        var starts = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var target in targets.Where(target => IsPrompt(states[target]))) {
            prompt.Add(target);
            queue.Enqueue(target);
        }

        while (queue.Count > 0) {
            foreach (var transition in GetTransitions(states[queue.Dequeue()])) {
                if (transition.ToState is not { } to || !states.TryGetValue(to, out var next) || idle.Contains(to)) {
                    continue;
                }

                if (IsPrompt(next)) {
                    if (prompt.Add(to)) {
                        queue.Enqueue(to);
                    }
                } else if (!CancelEvents.Contains(transition.EventName ?? "")) {
                    starts.Add(to);
                }
            }
        }

        // Wishes, rewards, conversations and travel belong to each player or to other rules
        var actions = Reach(states, targets, idle).SelectMany(name => GetActions(states[name]));
        if (actions.Any(action => action.GetType().Namespace == "QuestPlaymakerActions" ||
                                  HasPrefix(action, GivePrefixes) ||
                                  action.GetType().Name.StartsWith("RunDialogue", StringComparison.Ordinal) ||
                                  action.GetType().Name.StartsWith("BeginSceneTransition", StringComparison.Ordinal))) {
            return null;
        }

        if (starts.Count == 0 ||
            !prompt.SelectMany(name => GetActions(states[name])).Any(action => HasPrefix(action, PaymentPrefixes))) {
            return null;
        }

        return new CoopMechanism(
            idle,
            prompt,
            starts.ToDictionary(start => start, start => Reach(states, [start], idle), StringComparer.Ordinal)
        );
    }

    /// <summary>
    /// Whether the FSM belongs to a character, whose conversations aren't mechanisms, or to a bench, which has rules of
    /// its own.
    /// </summary>
    private static bool IsCharacterOrBench(Fsm fsm, IEnumerable<FsmState> states) {
        var gameObject = fsm.GameObject;
        if (gameObject != null) {
            if (gameObject.name.Contains("NPC")) {
                return true;
            }

            foreach (var component in gameObject.GetComponents<MonoBehaviour>()) {
                var name = component == null ? "" : component.GetType().Name;
                if (name != nameof(PlayMakerNPC) && (name.Contains("NPC") || name.Contains("Npc"))) {
                    return true;
                }
            }
        }

        return states.SelectMany(GetTransitions)
            .Concat(fsm.GlobalTransitions ?? [])
            .Any(transition => transition.EventName?.StartsWith("BENCHREST", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Whether a state belongs to a prompt: it pays, talks or moves the hero, or waits for a dialogue or a yes/no box.
    /// </summary>
    private static bool IsPrompt(FsmState state) {
        return GetActions(state).Any(action => HasPrefix(action, PromptPrefixes)) ||
               GetTransitions(state).Any(transition => WaitEvents.Contains(transition.EventName ?? ""));
    }

    /// <summary>
    /// The states that the FSM can go through from the given states until it waits for the interact button again.
    /// </summary>
    private static HashSet<string> Reach(
        Dictionary<string, FsmState> states,
        IEnumerable<string> starts,
        HashSet<string> idle
    ) {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in starts) {
            if (states.ContainsKey(start) && reached.Add(start)) {
                queue.Enqueue(start);
            }
        }

        while (queue.Count > 0) {
            foreach (var transition in GetTransitions(states[queue.Dequeue()])) {
                if (transition.ToState is { } to && states.ContainsKey(to) && !idle.Contains(to) && reached.Add(to)) {
                    queue.Enqueue(to);
                }
            }
        }

        return reached;
    }

    private static IEnumerable<FsmStateAction> GetActions(FsmState state) {
        return (state.Actions ?? []).Where(action => action != null && action.Enabled);
    }

    private static FsmTransition[] GetTransitions(FsmState state) => state.Transitions ?? [];

    private static bool HasPrefix(FsmStateAction action, string[] prefixes) {
        var name = action.GetType().Name;
        foreach (var prefix in prefixes) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
