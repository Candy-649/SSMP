using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Parts that a creature lets go of into the room, each known by the creature it was under and its place there. Some
/// parts let go of themselves as they first wake: the spires that a coral brute raises as it stamps take themselves off
/// it and wait loose in the room until they are told to rise again under it, and each copy of a brute does the same in
/// the other game. Named by their place in the room, the spires of several brutes stood there under one name, in
/// another order in each game, so the scene host's brute told the spires of another brute in the other game: they rose
/// under that one, and the brute's own were left where they waited, unseen, for the switches of their hurting parts to
/// follow the host's (DamageHeroComponent), which stayed on for good when the brute died before its spires sank
/// (USER 10-31: "我朋友被看不到的珊瑚打到了"). A part that a creature let go of is named by its place under the
/// creature instead, which is the same in every game, and found there in the copy.
/// </summary>
internal static class LetGoParts {
    /// <summary>
    /// Where a part was before it was last let go of: the creature it was under, and its place there.
    /// </summary>
    private sealed class Origin(GameObject creature, string place) {
        public readonly GameObject Creature = creature;
        public readonly string Place = place;
    }

    /// <summary>
    /// Where each part that was let go of was under its creature the last time, kept as long as the part is. Things of
    /// a pool come round again in other rooms and for other creatures, so what is said of one is checked against
    /// <see cref="ByPlace"/>, which holds this room's alone.
    /// </summary>
    private static readonly ConditionalWeakTable<GameObject, Origin> Origins = new();

    /// <summary>
    /// The parts that the creatures of this room let go of, by creature and place: the last one from each place.
    /// </summary>
    private static readonly Dictionary<(GameObject Creature, string Place), GameObject> ByPlace = new();

    /// <summary>
    /// The places that were said in the log as the scene host's creature named them, once a room each.
    /// </summary>
    private static readonly HashSet<string> SaidFound = [];

    /// <summary>
    /// The hooks on the actions that give an object another parent.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// Whether something went wrong here already and was said in the log, which is said once in each room.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Registers the hooks on the actions that give an object another parent, to note a part as it is let go of.
    /// </summary>
    public static void RegisterHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        AddHook(
            typeof(SetParent).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null),
            new Action<Action<SetParent>, SetParent>(OnSetParent)
        );
        AddHook(
            typeof(SetTransformParent).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null),
            new Action<Action<SetTransformParent>, SetTransformParent>(OnSetTransformParent)
        );
    }

    /// <summary>
    /// Hooks a method of an action that gives an object another parent.
    /// </summary>
    private static void AddHook(MethodInfo? method, Delegate hook) {
        if (method == null) {
            Logger.Error("Could not find how an action gives an object a parent; parts let go of are named by the room");
            return;
        }

        try {
            Hooks.Add(new Hook(method, hook));
        } catch (Exception e) {
            Logger.Error($"Could not hook {method.DeclaringType?.Name}.{method.Name}:\n{e}");
        }
    }

    private static void OnSetParent(Action<SetParent> orig, SetParent self) {
        Note(self.Fsm, self.gameObject, self.parent);
        orig(self);
    }

    private static void OnSetTransformParent(Action<SetTransformParent> orig, SetTransformParent self) {
        Note(self.Fsm, self.gameObject, self.parent);
        orig(self);
    }

    /// <summary>
    /// Notes where the object of an action was under its creature as the action is about to give it a parent. Nothing
    /// here may keep the game's own action from running.
    /// </summary>
    private static void Note(Fsm? fsm, FsmOwnerDefault? target, FsmGameObject? newParent) {
        try {
            var part = fsm == null || target == null ? null : fsm.GetOwnerDefaultTarget(target);
            NoteLetGo(part, newParent?.Value, part != null && fsm!.GameObject == part);
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Notes where an object was under its creature as it is about to be given a parent outside the creature: by an
    /// action of the game, or by the copy's replay of one, which moves the copy's own part itself. Each time it is let
    /// go of, as a thing of a pool may be another creature's the next time.
    /// </summary>
    /// <param name="part">The object, still where it was.</param>
    /// <param name="parent">The parent it is about to be given, or null for none.</param>
    /// <param name="itself">Whether the part lets go of itself, for the log.</param>
    public static void NoteLetGo(GameObject? part, GameObject? parent, bool itself = false) {
        try {
            if (part == null || Entity.IsEntityObject(part)) {
                return;
            }

            Transform? creature = null;
            for (var current = part.transform.parent; current != null; current = current.parent) {
                if (Entity.IsEntityObject(current.gameObject)) {
                    creature = current;
                    break;
                }
            }

            if (creature == null || parent != null && parent.transform.IsChildOf(creature)) {
                return;
            }

            var place = PlaceUnder(creature, part.transform);
            Origins.Remove(part);
            Origins.Add(part, new Origin(creature.gameObject, place));
            ByPlace[(creature.gameObject, place)] = part;
            Logger.Info(
                $"[Let go] '{creature.name}' lets go of its part '{place}' " +
                (parent == null ? "into the room" : $"to go under '{parent.name}'") +
                $"{(itself ? ", which lets go of itself" : "")}; it goes by that place from now on"
            );
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Says in the log, once a room, that a part let go of could not be noted.
    /// </summary>
    private static void SayFailed(Exception e) {
        if (!_failed) {
            _failed = true;
            Logger.Warn($"Could not note a part that a creature lets go of: {e}");
        }
    }

    /// <summary>
    /// The place of a part under its creature, the way Transform.Find takes it.
    /// </summary>
    private static string PlaceUnder(Transform creature, Transform part) {
        var names = new List<string>();
        for (var current = part; current != null && current != creature; current = current.parent) {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// The place that something had under a creature before the creature let go of it in this room, or of the part it
    /// is under: the part's place and the way down to it from there. Null for anything the creature never let go of
    /// here or still holds.
    /// </summary>
    /// <param name="creature">The creature, whose FSM works on it.</param>
    /// <param name="subject">What the FSM works on.</param>
    public static string? PlaceOf(GameObject? creature, GameObject subject) {
        if (creature == null || subject.transform.IsChildOf(creature.transform)) {
            return null;
        }

        string? below = null;
        for (var current = subject.transform; current != null; current = current.parent) {
            if (Origins.TryGetValue(current.gameObject, out var origin)) {
                if (origin.Creature != creature ||
                    !ByPlace.TryGetValue((creature, origin.Place), out var latest) || latest != current.gameObject) {
                    return null;
                }

                return below == null ? origin.Place : origin.Place + "/" + below;
            }

            below = below == null ? current.name : current.name + "/" + below;
        }

        return null;
    }

    /// <summary>
    /// The part of a creature's copy that had a place under it: one the copy let go of from there, or something under
    /// such a part, or else what is at that place under the copy still.
    /// </summary>
    /// <param name="creature">The copy of the creature.</param>
    /// <param name="place">The place the part had under the creature, as <see cref="PlaceOf"/> gave it.</param>
    /// <returns>The part, or null if the copy has none.</returns>
    public static GameObject? Find(GameObject creature, string place) {
        GameObject? found = null;
        var how = "which the copy has not got, so it keeps what it holds";
        for (var end = place.Length; end > 0; end = place.LastIndexOf('/', end - 1)) {
            if (!ByPlace.TryGetValue((creature, place.Substring(0, end)), out var letGo) || letGo == null) {
                continue;
            }

            var under = end == place.Length ? letGo.transform : letGo.transform.Find(place.Substring(end + 1));
            if (under != null) {
                found = under.gameObject;
                how = "which the copy let go of too";
            }

            break;
        }

        if (found == null && creature.transform.Find(place) is { } held) {
            found = held.gameObject;
            how = "still under the copy";
        }

        if (SaidFound.Add(creature.name + "/" + place)) {
            Logger.Info(
                $"[Let go] The part '{place}' that the scene host's '{creature.name}' let go of is the copy's own, " +
                $"{how} (said once a room)"
            );
        }

        return found;
    }

    /// <summary>
    /// Whether something that a creature let go of in this room runs by itself: the part that was let go of, or
    /// something on the way down from it, has a state machine of its own, which each game runs as the creature tells
    /// it, so that what it switches is its own business there (see DamageHeroComponent). False for anything the
    /// creature still holds, and for a part that runs nothing of its own, which goes by the creature's word.
    /// </summary>
    /// <param name="thing">The thing.</param>
    /// <param name="creature">The creature it came from.</param>
    public static bool RunsByItself(Transform thing, GameObject? creature) {
        if (creature == null || thing.IsChildOf(creature.transform)) {
            return false;
        }

        var runs = false;
        for (var current = thing; current != null; current = current.parent) {
            runs |= current.GetComponent<PlayMakerFSM>() != null;
            if (Origins.TryGetValue(current.gameObject, out var origin)) {
                return runs && origin.Creature == creature;
            }
        }

        return false;
    }

    /// <summary>
    /// Forgets the parts of the room that was left.
    /// </summary>
    public static void Forget() {
        ByPlace.Clear();
        SaidFound.Clear();
        _failed = false;
    }
}
