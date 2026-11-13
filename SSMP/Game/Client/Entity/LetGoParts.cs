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
    /// Where a part was before it was last let go of: the creature it was under, its place there, and the frame in
    /// which it was let go of.
    /// </summary>
    private sealed class Origin(GameObject creature, string place, int frame) {
        public readonly GameObject Creature = creature;
        public readonly string Place = place;
        public readonly int Frame = frame;
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
    /// Whether the actions by which a creature let go of its parts into the room are said again just now, for a player
    /// who walked in after it let go of them (Entity.SendLetGoPartsAgain): what goes with each says where its part is
    /// in the room by then.
    /// </summary>
    public static bool SayingAgain { get; private set; }

    /// <summary>
    /// Has the actions that are written until it ends say where the parts they let go of are in the room (see
    /// <see cref="SayingAgain"/>).
    /// </summary>
    /// <returns>What ends it.</returns>
    public static IDisposable SayAgain() {
        SayingAgain = true;
        return new EndsSayingAgain();
    }

    /// <summary>
    /// Ends <see cref="SayAgain"/>.
    /// </summary>
    private sealed class EndsSayingAgain : IDisposable {
        public void Dispose() {
            SayingAgain = false;
        }
    }

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
        AddHook(
            typeof(GetGrandparent).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null),
            new Action<Action<GetGrandparent>, GetGrandparent>(OnGetGrandparent)
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
        ForgetTargetOwners(self.Fsm, self.gameObject);
    }

    private static void OnSetTransformParent(Action<SetTransformParent> orig, SetTransformParent self) {
        Note(self.Fsm, self.gameObject, self.parent);
        orig(self);
        ForgetTargetOwners(self.Fsm, self.gameObject);
    }

    /// <summary>
    /// Has whose targeting the object of an action keeps found again from where it is now that it has another parent
    /// (GamePatcher.ForgetTargetOwnersUnder).
    /// </summary>
    private static void ForgetTargetOwners(Fsm? fsm, FsmOwnerDefault? target) {
        var moved = fsm == null || target == null ? null : fsm.GetOwnerDefaultTarget(target);
        if (moved != null) {
            GamePatcher.ForgetTargetOwnersUnder(moved);
        }
    }

    private static void OnGetGrandparent(Action<GetGrandparent> orig, GetGrandparent self) {
        orig(self);
        try {
            AnswerFromWhereItWas(self);
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Gives a part that asks what it is under two steps up, in the frame in which the part above it let go of their
    /// creature, what that part was under in the creature. The parts of a boss's hands start by asking for the boss
    /// that way, to see which side of it they rise on, in the same frame as each hand lets go of the boss; in the
    /// scene host's game they asked first, but in the game of the copy the hands went first. There the parts got
    /// nothing, never rose, and so never let go of the hands either, hanging from them for the part of the fight in
    /// which the scene host's had come loose, turned over whenever the hands turned with the boss (USER 11-07 "那个
    /// 很多针的攻击，有时候是反的"). Which goes first in a frame is not the game's to say, so either way now gives the
    /// same. Only a part that the game finds nothing for is told: one whose part above went under something else is
    /// left with what the game found.
    /// </summary>
    private static void AnswerFromWhereItWas(GetGrandparent action) {
        var owner = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        var above = owner == null ? null : owner.transform.parent;
        if (above == null || action.storeResult.Value != null ||
            !Origins.TryGetValue(above.gameObject, out var origin) || origin.Frame != Time.frameCount) {
            return;
        }

        var cut = origin.Place.LastIndexOf('/');
        var grandparent = cut < 0
            ? origin.Creature.transform
            : origin.Creature.transform.Find(origin.Place.Substring(0, cut));
        action.storeResult.Value = grandparent.gameObject;
        Logger.Info(
            $"[Let go] '{owner!.name}' asked what it is under two steps up in the frame in which '{above.name}' let go " +
            $"of '{origin.Creature.name}', and is told '{grandparent.name}', where '{above.name}' was"
        );
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
        if (part == null || Entity.IsEntityObject(part) ||
            !TryFindHolder(part.transform, out var holder, out var creature, out var holderPlace) ||
            parent != null && (parent.transform.IsChildOf(holder) || parent.transform.IsChildOf(creature.transform))) {
            return;
        }

        var below = PlaceUnder(holder, part.transform);
        var place = holderPlace == null ? below : holderPlace + "/" + below;
        Origins.Remove(part);
        Origins.Add(part, new Origin(creature, place, Time.frameCount));
        ByPlace[(creature, place)] = part;
        Logger.Info(
            $"[Let go] '{holder.name}'" + (holderPlace == null ? "" : $", which '{creature.name}' let go of,") +
            $" lets go of its part '{below}' " +
            (parent == null ? "into the room" : $"to go under '{parent.name}'") +
            $"{(itself ? ", which lets go of itself" : "")}; it goes by its place under '{creature.name}', " +
            $"'{place}', from now on"
        );

        // A part that lets go of itself runs by itself in each game, and moves by itself from then on: on a copy, its
        // bodies get back the kind the game made them as, which the copy's build made still (EntityInitializer
        // .GiveBackBodyType). Left still, the blades of a boss's hands, which drop by their own weight as the boss
        // falls stunned, stayed up in the air in the game of the copy, poised to strike (USER 11-09 "…她的针会和她人一起
        // 落下去，我这里针没有落下去，保持在要出招的样子")
        if (itself) {
            foreach (var body in part.GetComponentsInChildren<Rigidbody2D>(true)) {
                EntityInitializer.GiveBackBodyType(body);
            }
        }
    }

    /// <summary>
    /// Finds what holds a part for its creature: the creature itself, or a part that the creature let go of in this
    /// room and that is not back under it. The blades of a boss's hands let go of their hand as they first rise, after
    /// the hands let go of the boss as the room starts. Loose, a blade was nobody's part, so the scene host's switches
    /// of a blade's hurting part went on the same blade in the other game (DamageHeroComponent), where it flew at
    /// another time: a blade there that was stuck in the ground or on its way back hurt the partner while the host's
    /// flew (USER 11-08 "…收回针的时候打到她了").
    /// </summary>
    /// <param name="part">The part.</param>
    /// <param name="holder">What holds the part: the creature, or the part that the creature let go of.</param>
    /// <param name="creature">The creature.</param>
    /// <param name="holderPlace">The place of the holder under the creature, or null if the holder is the creature.
    /// </param>
    /// <returns>Whether something holds the part for a creature.</returns>
    private static bool TryFindHolder(
        Transform part,
        out Transform holder,
        out GameObject creature,
        out string? holderPlace
    ) {
        for (var current = part.parent; current != null; current = current.parent) {
            if (Entity.IsEntityObject(current.gameObject)) {
                holder = current;
                creature = current.gameObject;
                holderPlace = null;
                return true;
            }

            if (Origins.TryGetValue(current.gameObject, out var origin) && origin.Creature != null &&
                !current.IsChildOf(origin.Creature.transform) &&
                ByPlace.TryGetValue((origin.Creature, origin.Place), out var latest) && latest == current.gameObject) {
                holder = current;
                creature = origin.Creature;
                holderPlace = origin.Place;
                return true;
            }
        }

        holder = null!;
        creature = null!;
        holderPlace = null;
        return false;
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
    /// Whether something is a part that the room's own object of a creature let go of, or under one. That object
    /// sleeps in the game that does not run the creature, and so do the parts it let go of: what the creature tells the
    /// room there is for the parts of the copy, as it is in the scene host's game for the creature's own. The parts of
    /// a boss's hands that the room's own boss let go of as the room started still took it, and rose, fell and came
    /// back where that boss slept.
    /// </summary>
    /// <param name="thing">The thing.</param>
    public static bool IsLetGoByTheRoomsOwn(Transform thing) {
        for (var current = thing; current != null; current = current.parent) {
            if (Origins.TryGetValue(current.gameObject, out var origin)) {
                return ByPlace.TryGetValue((origin.Creature, origin.Place), out var latest) &&
                       latest == current.gameObject && EntityProcessor.IsRoomObject(origin.Creature);
            }
        }

        return false;
    }

    /// <summary>
    /// The creature that let go of a part in this room, of what the part is, or of what it is under, while it is the
    /// latest part from its place there; null for anything else, which the creature still holds or nobody let go of.
    /// </summary>
    /// <param name="thing">The thing.</param>
    public static GameObject? CreatureOf(GameObject thing) {
        for (var current = thing.transform; current != null; current = current.parent) {
            if (Origins.TryGetValue(current.gameObject, out var origin)) {
                return ByPlace.TryGetValue((origin.Creature, origin.Place), out var latest) &&
                       latest == current.gameObject
                    ? origin.Creature
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds the parts that a creature of this room let go of to a list, the latest from each place.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="parts">The list.</param>
    public static void PartsOf(GameObject creature, List<GameObject> parts) {
        foreach (var pair in ByPlace) {
            if (pair.Key.Creature == creature && pair.Value != null) {
                parts.Add(pair.Value);
            }
        }
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
