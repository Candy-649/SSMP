using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Writes in the log what becomes of the items of wishes that the bodies of creatures drop (SpecialQuestItemVariant):
/// whether a body drops one and why it doesn't, and whether the one it dropped is taken or goes away untaken, with what
/// took it away. A player saw one never show, or vanish, in the game that ran the room while the other game's body
/// dropped it, and nothing in the log could tell which. It changes nothing in the game.
/// </summary>
internal static class WishDrops {
    /// <summary>
    /// How many frames of what switched an untaken item off are written down.
    /// </summary>
    private const int MostFrames = 12;

    /// <summary>
    /// The hooks, kept so they stay alive.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// The items that bodies dropped here, each with what dropped it, until taken.
    /// </summary>
    private static readonly ConditionalWeakTable<CollectableItemPickup, string> Dropped = new();

    /// <summary>
    /// Whether looking at a drop went wrong, which is only logged once.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Registers the hooks on a body's drop and on the item it drops.
    /// </summary>
    public static void RegisterHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        AddHook(
            typeof(SpecialQuestItemVariant).GetMethod("Start", flags, null, Type.EmptyTypes, null),
            new Action<Action<SpecialQuestItemVariant>, SpecialQuestItemVariant>(OnDropStart)
        );
        AddHook(
            typeof(CollectableItemPickup).GetMethod("DoPickupAction", flags, null, [typeof(bool)], null),
            new Func<Func<CollectableItemPickup, bool, bool>, CollectableItemPickup, bool, bool>(OnPickupAction)
        );
        AddHook(
            typeof(CollectableItemPickup).GetMethod("OnDisable", flags, null, Type.EmptyTypes, null),
            new Action<Action<CollectableItemPickup>, CollectableItemPickup>(OnPickupDisable)
        );
    }

    /// <summary>
    /// Hooks a method, or says in the log that it can't.
    /// </summary>
    private static void AddHook(MethodInfo? method, Delegate hook) {
        if (method == null) {
            Logger.Warn("Could not find a method of the items of wishes that bodies drop; what becomes of them is " +
                        "not logged");
            return;
        }

        try {
            Hooks.Add(new Hook(method, hook));
        } catch (Exception e) {
            Logger.Warn($"Could not hook {method.DeclaringType?.Name}.{method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// Hook on the start of a body's part that may drop the item of a wish: says whether it dropped it, and why not.
    /// </summary>
    private static void OnDropStart(Action<SpecialQuestItemVariant> orig, SpecialQuestItemVariant self) {
        var before = 0;
        string? why = null;
        try {
            before = self.GetComponentsInChildren<CollectableItemPickup>(true).Length;
            why = WhyNoDrop(self);
        } catch (Exception e) {
            SayFailed(e);
        }

        orig(self);

        try {
            var pickups = self.GetComponentsInChildren<CollectableItemPickup>(true);
            var where = $"'{PathOf(self.transform)}' at {Place(self.transform)} in {self.gameObject.scene.name}";
            if (pickups.Length > before) {
                var dropped = pickups[pickups.Length - 1];
                Dropped.Remove(dropped);
                Dropped.Add(dropped, where);
                Logger.Info($"[Wish drops] {where} dropped the item of {Describe(self.activeQuest)}");
            } else {
                Logger.Info(
                    $"[Wish drops] {where} dropped nothing: {why ?? "it rolled no drop"}; {Describe(self.activeQuest)}"
                );
            }
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Why a body's part won't drop the item of its wish, as its start looks at it, or null if it will.
    /// </summary>
    private static string? WhyNoDrop(SpecialQuestItemVariant variant) {
        var quest = variant.activeQuest;
        if (variant.isInactive) {
            return "it is switched off (the wave of an arena, or SSMP for a copy in one)";
        }

        if (quest == null) {
            return "it has no wish";
        }

        if (!quest.IsAccepted) {
            return "the wish is not accepted";
        }

        if (quest.IsCompleted) {
            return "the wish is completed";
        }

        return quest.CanComplete ? "the wish has all it needs" : null;
    }

    /// <summary>
    /// A wish with its state and how much of each thing it needs this game's player has.
    /// </summary>
    private static string Describe(FullQuestBase? quest) {
        if (quest == null) {
            return "no wish";
        }

        var text = new StringBuilder($"'{quest.name}' (accepted {quest.IsAccepted}, completed {quest.IsCompleted}");
        try {
            var completion = quest.Completion;
            text.Append($", counted {completion.CompletedCount}");
            foreach (var target in quest.targets) {
                if (target.Counter is { } counter) {
                    text.Append($", {counter.name} {counter.GetCompletionAmount(completion)}/{target.Count}");
                }
            }
        } catch (Exception e) {
            text.Append($", counts unreadable: {e.GetType().Name}");
        }

        return text.Append(')').ToString();
    }

    /// <summary>
    /// Hook on taking an item: says that one a body dropped was taken here.
    /// </summary>
    private static bool OnPickupAction(
        Func<CollectableItemPickup, bool, bool> orig,
        CollectableItemPickup self,
        bool breakIfAtMax
    ) {
        var taken = orig(self, breakIfAtMax);
        try {
            if (Dropped.TryGetValue(self, out var where)) {
                // One turned down stays, and may still go away untaken
                if (taken) {
                    Dropped.Remove(self);
                }

                var what = taken ? "was taken" : "was not taken, as the game turned it down,";
                Logger.Info($"[Wish drops] The item that {where} dropped {what} at {Place(self.transform)}");
            }
        } catch (Exception e) {
            SayFailed(e);
        }

        return taken;
    }

    /// <summary>
    /// Hook on an item being switched off: says that one a body dropped went away untaken, with what switched it off.
    /// Leaving the room switches it off too, which the log says next.
    /// </summary>
    private static void OnPickupDisable(Action<CollectableItemPickup> orig, CollectableItemPickup self) {
        try {
            if (Dropped.TryGetValue(self, out var where)) {
                Dropped.Remove(self);
                var parent = self.transform.parent;
                Logger.Info(
                    $"[Wish drops] The item that {where} dropped went away untaken at {Place(self.transform)}, under " +
                    $"'{(parent != null ? PathOf(parent) : "nothing")}', switched off by:\n{Frames()}"
                );
            }
        } catch (Exception e) {
            SayFailed(e);
        }

        orig(self);
    }

    /// <summary>
    /// The calls that led here, without the hooks themselves.
    /// </summary>
    private static string Frames() {
        var text = new StringBuilder();
        var written = 0;
        foreach (var frame in new StackTrace(2, false).GetFrames() ?? []) {
            if (frame.GetMethod() is not { } method || written >= MostFrames) {
                continue;
            }

            text.Append($"    {method.DeclaringType?.FullName}.{method.Name}\n");
            written++;
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Where something is in the world.
    /// </summary>
    private static string Place(Transform thing) {
        var position = thing.position;
        return $"({position.x:0.00}, {position.y:0.00})";
    }

    /// <summary>
    /// Where something sits in the world, written out in full.
    /// </summary>
    private static string PathOf(Transform thing) {
        var path = thing.name;
        for (var parent = thing.parent; parent != null; parent = parent.parent) {
            path = parent.name + "/" + path;
        }

        return path;
    }

    /// <summary>
    /// Says in the log, once, that looking at a drop went wrong.
    /// </summary>
    private static void SayFailed(Exception e) {
        if (_failed) {
            return;
        }

        _failed = true;
        Logger.Warn($"Could not look at the item of a wish that a body dropped:\n{e}");
    }
}
