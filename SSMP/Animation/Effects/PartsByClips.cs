using System;
using System.Collections.Generic;
using System.Linq;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The parts of the hero that the game shows as part of a move - the needles and threads of a super jump, the flicks
/// of the harpoon, the puffs of climbing a wall, the flash of a charged slash - shown on the characters of other players
/// by the clips of those characters. Sent apart from the clips, as they were, a part showed out of step with its
/// character whenever its word got lost or came late: the needles at the sides of a super jump went up and then showed
/// at its sides again for the rest of the jump (USER 10-31 "那个针是先演了上去的动画结果又闪回了之前的位置").
///
/// Nothing of these parts goes over the network now. The clips of a character reach every other player counted, and a
/// clip that got lost is put right from what its player says their character plays, so each game brings the parts of a
/// move out and makes them go by those clips, the way the hero's own state machines switch them as they play the clips
/// (USER 10-31 "超级跳只需要两个消息，蓄力和发射。其他的动画都绑定在这两个消息上"). The parts that go by what only the
/// player's own game knows, like how much silk they have or how long they held a button, still go by their own words
/// (see <see cref="HeroChildEffects"/>).
/// </summary>
internal static class PartsByClips {
    /// <summary>
    /// How late a clip that was put right may be for a part that comes out once and ends by itself to come out with
    /// it, in seconds: later, it would show out of step with the clip, after the moment it belongs to.
    /// </summary>
    internal const float LateBy = 0.25f;

    /// <summary>
    /// The layer of the ground, walls and roofs.
    /// </summary>
    private const int TerrainMask = 1 << 8;

    /// <summary>
    /// The clips of a super jump, from the charge to the catch.
    /// </summary>
    private static readonly string[] SuperJump = [
        "Super Jump Antic",
        "Super Jump Antic Cancel",
        "Super Jump Throw",
        "Super Jump Throw Wait",
        "Super Jump Jump Antic",
        "Super Jump Loop",
        "Super Jump Hit Roof",
        "Super Jump Hit Roof Q",
        "Super Jump Loop Cancel",
        "Super Jump Catch Cancel"
    ];

    /// <summary>
    /// The clips of a character that is hit, stunned or killed, at which the game cancels what the hero was doing and
    /// switches off the parts of that move.
    /// </summary>
    private static readonly string[] Hurt = ["Recoil", "Stun", "Death"];

    /// <summary>
    /// The path of the needle that a super jump throws up, whose place across the character the needle stuck up above
    /// is put at.
    /// </summary>
    private const string ThrownNeedlePath = "Special Attacks/Super Jump Needle Throw";

    /// <summary>
    /// How each part goes by the clips of the character, as the hero's state machines switch it.
    /// </summary>
    private static readonly Rule[] Rules = [
        // The needles that come out at both sides of a super jump as it charges: they loop while it charges, go up as
        // the needle is thrown, and go at once as the charge is let go too soon or broken off
        new() {
            Path = "Effects/Super Jump Antic Effect L", On = ["Super Jump Antic"], PartClip = "Super Jump Antic Effect",
            Through = [], EndAt = ["Super Jump Throw"], EndClip = "Super Jump Antic Effect End"
        },
        new() {
            Path = "Effects/Super Jump Antic Effect R", On = ["Super Jump Antic"], PartClip = "Super Jump Antic Effect",
            Through = [], EndAt = ["Super Jump Throw"], EndClip = "Super Jump Antic Effect End"
        },

        // What bursts from the ground as the charge begins, which the game takes away as the charge is let go too soon
        new() {
            Path = "Effects/Super Jump Extra Ground Effect", On = ["Super Jump Antic"],
            Through = SuperJump.Where(clip => clip != "Super Jump Antic Cancel").ToArray()
        },

        // The glow of a full charge, once the game's charge time is over and while the charge is still held
        new() {
            Path = "Effects/Super Jump Charged", On = ["Super Jump Antic"], DelayFromHero = SuperJumpChargeTime,
            Through = []
        },

        // What bursts up as the needle is thrown, and the thread it draws up, which the game shows only when the roof
        // is further up than a short way
        new() { Path = "Effects/Super Jump Extra Throw Effect", On = ["Super Jump Throw"], Through = SuperJump },
        new() {
            Path = "Effects/Super Jump Thread", On = ["Super Jump Throw"], AtTrigger = 0, If = NoRoofWithin12,
            Through = SuperJump
        },

        // The needle stuck in the roof above, which stays there as the character goes up to it. A super jump that goes
        // on into the next room starts there at its flight, so the flight brings it out too
        new() {
            Path = "Special Attacks/Super Jump Needle Stick", On = ["Super Jump Jump Antic", "Super Jump Loop"],
            Through = [], Place = StickInRoof
        },

        // The thread the character goes up on, a moment after it sets off, hidden while a roof is close above
        new() {
            Path = "Effects/Super Jump Thread Loop", On = ["Super Jump Loop"], Delay = 0.1f, Through = [],
            WhileOut = HideThreadNearRoof
        },

        // What bursts as the character catches the needle, by the jump broken off or the needle that found no roof
        new() { Path = "Effects/Super Jump Catch Effect", On = ["Super Jump Loop Cancel", "Super Jump Catch Cancel"] },

        // The flicks of the harpoon as it is thrown, as the character dashes after it, and as it catches it
        new() { Path = "Effects/Hornet_harpoon_throw_effect", On = ["Harpoon Throw"], CutAt = Hurt },
        new() { Path = "Effects/Hornet_harpoon_dash", On = ["Harpoon Dash"], CutAt = Hurt },
        new() { Path = "Effects/Hornet_harpoon_grab_effect", On = ["Harpoon Catch", "Harpoon Catch Back"], CutAt = Hurt },

        // The burst of setting off up a wall, which the game starts over each time, the puff of going over its top, and
        // the hit of catching a ledge
        new() {
            Path = "Effects/Hornet_Wall_Scramble_Burst_Effect", On = ["Wall Scramble", "Wall Scramble Quickened"],
            Restarts = true, CutAt = Hurt
        },
        new() { Path = "Effects/wall_launch_puff", On = ["Wall Scramble Mantle"] },
        new() { Path = "Effects/clamber_impact_effects", On = ["Mantle Cling"] },

        // The burst of a charged down attack, and of setting off on the tool that carries the hero along the ground,
        // which the game takes away as the hero turns to go on
        new() { Path = "Effects/Downspike Burst", On = ["DownSpike Charged"] },
        new() { Path = "Tool Effects/Scuttle Burst", On = ["Scuttle Start"], CutAt = ["Scuttle TurnToLoop"] },

        // The flash of a charged slash, of the needle art of the beast crest and of the silk storm in the air, and of a
        // quick craft as it bursts, at the second frame of its clip that the game waits for
        new() {
            Path = "Special Attacks/Sphere Flash", On = ["Slash_Charged", "NeedleArt Dash", "AirSphere Attack"],
            CutAt = Hurt
        },
        new() {
            Path = "Special Attacks/Sphere Flash", On = ["Quick Craft Ground", "Quick Craft Air"], AtTrigger = 1,
            CutAt = Hurt
        },

        // The silk of a quick craft, which the game starts over each time, and the flash of the tools made anew on a
        // bench as the crafting is over
        new() {
            Path = "Tool Effects/QuickCraft Silk", On = ["Quick Craft Ground", "Quick Craft Air"], Restarts = true,
            CutAt = Hurt
        },
        new() { Path = "Tool Effects/Craft Flash", On = ["Sit Craft"], AtEnd = true },

        // The flash of the taunt with the rings, at the frame of its clip that the game waits for
        new() { Path = "Effects/Taunt Rings Flash", On = ["Taunt Rings"], AtTrigger = 0 },

        // The puff of a wall jump, and the burst and the bump of dashing through water
        new() { Path = "Effects/Wall Puff", On = ["Walljump", "Walljump Somersault"] },
        new() { Path = "Effects/Water Dash Burst", On = ["Swim Dash"] },
        new() { Path = "Effects/Swimp Bonk Effect", On = ["Swim Dash Bonk"] },

        // The cast of the shaman crest's blade as it slashes out of a dash, whose clip the beast crest plays too
        new() {
            Path = "Attacks/Shaman/Shaman_blade_cast_effect DashSlash", On = ["Dash Attack Slash"],
            ForCrest = crest => crest == CrestType.Shaman
        }
    ];

    /// <summary>
    /// The parts that go by the clips of the character, by their path under the hero. <see cref="HeroChildEffects"/>
    /// keeps their places in its list, so that their copies go when a character leaves the room, but neither watches
    /// nor sends them.
    /// </summary>
    public static readonly string[] Paths = Rules.Select(rule => rule.Path).Distinct().ToArray();

    /// <summary>
    /// The rules by the clips of the character that bring their parts out.
    /// </summary>
    private static readonly Dictionary<string, Rule[]> RulesByClip = Rules
        .SelectMany(rule => rule.On.Select(clip => (clip, rule)))
        .GroupBy(pair => pair.clip, pair => pair.rule)
        .ToDictionary(group => group.Key, group => group.ToArray());

    /// <summary>
    /// The parts that are to come out a while into the clip of a character, until they do or the character starts
    /// another clip.
    /// </summary>
    private static readonly List<Pending> Comings = [];

    /// <summary>
    /// The parts that are out and that the next clips of the character or each frame may still change: the parts that
    /// go by the clips of the character, and the parts that are held in place or looked after while they are out.
    /// </summary>
    private static readonly List<Showing> Out = [];

    /// <summary>
    /// Brings out the parts of the character of another player that come with the clip it starts, and makes the parts
    /// go that do not stay through it.
    /// </summary>
    /// <param name="playerObject">The character.</param>
    /// <param name="id">The ID of the player, for the log.</param>
    /// <param name="clipName">The name of the clip that the character starts.</param>
    /// <param name="clip">The clip as the character plays it, which may be the clip of its crest.</param>
    /// <param name="crestType">The crest of the player.</param>
    /// <param name="startTime">How far into the clip it starts, in seconds: more than 0 for a clip that was put right
    /// after its change got lost.</param>
    public static void OnCharacterClip(
        GameObject playerObject,
        ushort id,
        string clipName,
        tk2dSpriteAnimationClip clip,
        CrestType crestType,
        float startTime
    ) {
        // A part that was still to come out with another clip, or with this clip started before, does not
        if (Comings.Count > 0) {
            Comings.RemoveAll(coming => coming.Character == playerObject);
        }

        for (var i = Out.Count - 1; i >= 0; i--) {
            var showing = Out[i];
            if (showing.Character != playerObject) {
                continue;
            }

            if (showing.Part == null || !showing.Part.activeSelf) {
                Out.RemoveAt(i);
                continue;
            }

            // Brought out again by this clip, which is for what follows
            if (Array.IndexOf(showing.Rule.On, clipName) >= 0) {
                continue;
            }

            if (LeaveOrGo(showing, id, clipName)) {
                Out.RemoveAt(i);
            }
        }

        if (!RulesByClip.TryGetValue(clipName, out var rules)) {
            return;
        }

        foreach (var rule in rules) {
            if (rule.ForCrest == null || rule.ForCrest(crestType)) {
                Start(playerObject, id, rule, clipName, clip, startTime);
            }
        }
    }

    /// <summary>
    /// Brings the parts out whose time has come, and looks after the parts that are out, once a frame.
    /// </summary>
    public static void Tick() {
        if (Comings.Count > 0) {
            var now = Time.time;
            for (var i = Comings.Count - 1; i >= 0; i--) {
                var coming = Comings[i];
                if (coming.Character == null) {
                    Comings.RemoveAt(i);
                    continue;
                }

                if (now < coming.At) {
                    continue;
                }

                Comings.RemoveAt(i);
                ComeOut(coming.Character, coming.Id, coming.Rule, coming.ClipName);
            }
        }

        for (var i = Out.Count - 1; i >= 0; i--) {
            var showing = Out[i];
            if (showing.Character == null || showing.Part == null || !showing.Part.activeSelf) {
                Out.RemoveAt(i);
                continue;
            }

            if (showing.HeldAt is { } heldAt) {
                showing.Part.transform.position = heldAt;
            }

            showing.Rule.WhileOut?.Invoke(showing.Character, showing.Part);
        }
    }

    /// <summary>
    /// Forgets what is out and what is to come out on a character that leaves the room, whose copies are switched off
    /// with the other copied parts of the hero (see <see cref="HeroChildEffects.ResetCopies"/>).
    /// </summary>
    /// <param name="playerObject">The character.</param>
    public static void Reset(GameObject playerObject) {
        Comings.RemoveAll(coming => coming.Character == playerObject);
        Out.RemoveAll(showing => showing.Character == playerObject);
    }

    /// <summary>
    /// Makes a part that is out go, or play its end, as its character starts a clip that does not bring it out, unless
    /// it stays through that clip.
    /// </summary>
    /// <returns>Whether the part went.</returns>
    private static bool LeaveOrGo(Showing showing, ushort id, string clipName) {
        var rule = showing.Rule;
        var cut = rule.Through != null
            ? Array.IndexOf(rule.Through, clipName) < 0
            : rule.CutAt != null && Array.IndexOf(rule.CutAt, clipName) >= 0;
        if (!cut) {
            return false;
        }

        var part = showing.Part;
        part.TryGetComponent<tk2dSpriteAnimator>(out var animator);
        if (rule.EndClip != null && animator != null) {
            // Playing its end already, which it finishes whatever the character does next, as in the game
            if (animator.CurrentClip != null && animator.CurrentClip.name == rule.EndClip) {
                return false;
            }

            var endClip = Array.IndexOf(rule.EndAt, clipName) >= 0 ? animator.GetClipByName(rule.EndClip) : null;
            if (endClip != null) {
                animator.AnimationCompleted = OnEndClipDone;
                animator.Play(endClip);
                Logger.Info($"[Parts] Player {id} starts '{clipName}': '{rule.Path}' plays its end and goes");
                return false;
            }
        }

        part.SetActive(false);
        Logger.Info($"[Parts] Player {id} starts '{clipName}': '{rule.Path}' goes");
        return true;
    }

    /// <summary>
    /// Brings a part of a character out with the clip it starts, at once or a while into the clip.
    /// </summary>
    private static void Start(
        GameObject playerObject,
        ushort id,
        Rule rule,
        string clipName,
        tk2dSpriteAnimationClip clip,
        float startTime
    ) {
        float into;
        if (rule.AtEnd) {
            if (clip.frames == null || clip.frames.Length == 0 || clip.fps <= 0f) {
                return;
            }

            into = clip.frames.Length / clip.fps;
        } else if (rule.AtTrigger is { } trigger) {
            if (TriggerTime(clip, trigger) is not { } triggerTime) {
                WarnOnce(rule, $"'{clipName}' has no frame for '{rule.Path}' to come out at");
                return;
            }

            into = triggerTime;
        } else if (rule.DelayFromHero != null) {
            if (rule.DelayFromHero() is not { } delay) {
                WarnOnce(rule, $"could not read from the hero when '{rule.Path}' comes out with '{clipName}'");
                return;
            }

            into = delay;
        } else {
            into = rule.Delay;
        }

        var wait = into - startTime;
        if (wait > 0f) {
            Comings.Add(new Pending(playerObject, id, rule, clipName, Time.time + wait));
            return;
        }

        // Its moment is over already for a clip that was put right late: a part that stays out until it is made to
        // go is out by now, while a part that ends by itself comes out only when its moment was a short while ago
        if (-wait > LateBy && !IsLasting(rule)) {
            return;
        }

        ComeOut(playerObject, id, rule, clipName);
    }

    /// <summary>
    /// Brings a part of a character out from its start, unless it stays out until it is made to go and is out already
    /// for this move.
    /// </summary>
    private static void ComeOut(GameObject playerObject, ushort id, Rule rule, string clipName) {
        if (rule.If != null && !rule.If(playerObject)) {
            Logger.Info($"[Parts] Player {id} starts '{clipName}': '{rule.Path}' does not come out where it is");
            return;
        }

        var part = HeroChildEffects.GetCopy(playerObject, rule.Path, true);
        if (part == null) {
            return;
        }

        part.TryGetComponent<tk2dSpriteAnimator>(out var animator);
        var partClip = rule.PartClip != null && animator != null ? animator.GetClipByName(rule.PartClip) : null;

        // A part that is out already goes on as it is, as the game switches on only a part that is off, unless the game
        // switches it off and on to start it over. A part that stays out until it is made to go and plays a clip of
        // the move starts over only when it has gone on to another clip, like the needles going up when a new charge
        // begins
        if (part.activeSelf && !rule.Restarts &&
            (!IsLasting(rule) || partClip == null || animator!.IsPlaying(partClip))) {
            return;
        }

        Vector3? heldAt = null;
        if (rule.Place != null) {
            heldAt = rule.Place(playerObject, part);
            if (heldAt == null) {
                Logger.Info($"[Parts] Player {id} starts '{clipName}': '{rule.Path}' has no place to come out at");
                return;
            }
        }

        HeroChildEffects.Restart(part);
        if (partClip != null) {
            animator!.PlayFromFrame(partClip, 0);
        }

        if (heldAt is { } place) {
            part.transform.position = place;
        }

        if (rule.Through != null || rule.CutAt != null || heldAt != null || rule.WhileOut != null) {
            Out.RemoveAll(showing => showing.Part == part);
            Out.Add(new Showing(playerObject, part, rule, heldAt));
            rule.WhileOut?.Invoke(playerObject, part);
        }

        var where = heldAt is { } at
            ? $" at ({at.x:0.##}, {at.y:0.##}), the character at " +
              $"({playerObject.transform.position.x:0.##}, {playerObject.transform.position.y:0.##})"
            : "";
        Logger.Info($"[Parts] Player {id} starts '{clipName}': '{rule.Path}' comes out{where}");
    }

    /// <summary>
    /// Says once for a rule why its part cannot come out, rather than at every clip of every character.
    /// </summary>
    private static void WarnOnce(Rule rule, string why) {
        if (rule.Warned) {
            return;
        }

        rule.Warned = true;
        Logger.Warn($"[Parts] The part does not come out on other characters: {why}");
    }

    /// <summary>
    /// Whether a part stays out until it is made to go, rather than switching itself off when it is done.
    /// </summary>
    private static bool IsLasting(Rule rule) {
        if (rule.Lasting is { } lasting) {
            return lasting;
        }

        var local = HeroController.instance != null ? HeroController.instance.transform.Find(rule.Path) : null;
        if (local == null) {
            return false;
        }

        rule.Lasting = !HeroChildEffects.EndsByItself(local.gameObject);
        return rule.Lasting.Value;
    }

    /// <summary>
    /// How long into a clip its frame with an event comes that the game waits for: the first such frame, or a later
    /// one for a state that waits for the next event after the first.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="index">Which of the frames with an event, from 0.</param>
    /// <returns>The time in seconds, or null when the clip has no such frame.</returns>
    private static float? TriggerTime(tk2dSpriteAnimationClip clip, int index) {
        if (clip.frames == null || clip.fps <= 0f) {
            return null;
        }

        for (var i = 0; i < clip.frames.Length; i++) {
            if (clip.frames[i].triggerEvent && index-- == 0) {
                return i / clip.fps;
            }
        }

        return null;
    }

    /// <summary>
    /// Switches a part off as its end is over, as it would be gone in the game until its move is over.
    /// </summary>
    private static void OnEndClipDone(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip) {
        if (clip == null) {
            return;
        }

        foreach (var rule in Rules) {
            if (rule.EndClip == clip.name) {
                animator.gameObject.SetActive(false);
                return;
            }
        }
    }

    /// <summary>
    /// How long the game's super jump charges before it is full, read from the local hero, which charges for as long.
    /// </summary>
    private static float? SuperJumpChargeTime() {
        var fsm = HeroController.instance != null ? HeroController.instance.superJumpFSM : null;
        var chargeTime = fsm != null ? fsm.FsmVariables.FindFsmFloat("Charge Time") : null;
        return chargeTime?.Value;
    }

    /// <summary>
    /// Whether no roof is within the short way above a character past which the game shows the thread of a super
    /// jump.
    /// </summary>
    private static bool NoRoofWithin12(GameObject playerObject) {
        var hit = Physics2D.Raycast(playerObject.transform.position, Vector2.up, 350f, TerrainMask);
        return hit.collider == null || hit.distance > 12f;
    }

    /// <summary>
    /// Where the game puts the needle that sticks in the roof for a super jump: at the roof that its ray straight up
    /// from the character finds, across from where the thrown needle flew up. Nowhere when no roof is found, as the
    /// game then lets the jump fall through.
    /// </summary>
    private static Vector3? StickInRoof(GameObject playerObject, GameObject part) {
        var from = playerObject.transform.position;
        var hit = Physics2D.Raycast(from, Vector2.up, 350f, TerrainMask);
        if (hit.collider == null) {
            return null;
        }

        // The thrown needle flies straight up from its place under the hero, which goes with the way the character faces
        var x = from.x;
        var thrown = HeroController.instance != null ? HeroController.instance.transform.Find(ThrownNeedlePath) : null;
        if (thrown != null && part.transform.parent != null) {
            x = part.transform.parent.TransformPoint(new Vector3(thrown.localPosition.x, 0f, 0f)).x;
        }

        return new Vector3(x, hit.point.y, part.transform.position.z);
    }

    /// <summary>
    /// Hides the thread that a character goes up on while a roof is close above it, as the game does each frame.
    /// </summary>
    private static void HideThreadNearRoof(GameObject playerObject, GameObject part) {
        if (!part.TryGetComponent<MeshRenderer>(out var renderer)) {
            return;
        }

        var hit = Physics2D.Raycast(playerObject.transform.position, Vector2.up, 10f, TerrainMask);
        renderer.enabled = hit.collider == null;
    }

    /// <summary>
    /// How a part goes by the clips of the character.
    /// </summary>
    private sealed class Rule {
        /// <summary>
        /// The path of the part under the hero.
        /// </summary>
        public string Path = "";

        /// <summary>
        /// The clips of the character that bring the part out as they start.
        /// </summary>
        public string[] On = [];

        /// <summary>
        /// How long into the clip the part comes out, in seconds.
        /// </summary>
        public float Delay;

        /// <summary>
        /// Reads from the local hero how long into the clip the part comes out, in place of <see cref="Delay"/>.
        /// </summary>
        public Func<float?>? DelayFromHero;

        /// <summary>
        /// Which frame of the clip with an event the part comes out at, from 0, in place of <see cref="Delay"/>: the
        /// frame that the state of the game that switches it on waits for.
        /// </summary>
        public int? AtTrigger;

        /// <summary>
        /// Whether the part comes out as the clip is over, in place of <see cref="Delay"/>: for a part that the state
        /// of the game switches on once the clip it waits for is done.
        /// </summary>
        public bool AtEnd;

        /// <summary>
        /// The crests whose characters bring the part out with the clips, for a clip that more crests play; all when
        /// null.
        /// </summary>
        public Func<CrestType, bool>? ForCrest;

        /// <summary>
        /// The clip the part plays as it comes out, for a part that the game plays a clip on as it switches it on.
        /// </summary>
        public string? PartClip;

        /// <summary>
        /// The clips of the character that the part stays out through: as the character starts any other clip, it
        /// goes at once. Null for a part that the clips of the character never make go, which ends by itself.
        /// </summary>
        public string[]? Through;

        /// <summary>
        /// The clips of the character that cut the part short, for a part that otherwise ends by itself.
        /// </summary>
        public string[]? CutAt;

        /// <summary>
        /// The clips of the character at which the part plays <see cref="EndClip"/> and goes once it is over, rather
        /// than going at once.
        /// </summary>
        public string[] EndAt = [];

        /// <summary>
        /// The clip the part plays as it goes, at <see cref="EndAt"/>.
        /// </summary>
        public string? EndClip;

        /// <summary>
        /// Where in the world the part comes out, by the character and the part, for a part that the game takes off the
        /// hero and puts somewhere: it stays there while the character moves on. No place keeps the part from coming
        /// out.
        /// </summary>
        public Func<GameObject, GameObject, Vector3?>? Place;

        /// <summary>
        /// Whether the part starts over when its clip starts again while it is still out, as the game switches it off
        /// and on, rather than going on as it is.
        /// </summary>
        public bool Restarts;

        /// <summary>
        /// Whether it was said once in the log why the part cannot come out.
        /// </summary>
        public bool Warned;

        /// <summary>
        /// What must hold, by the character, for the part to come out.
        /// </summary>
        public Func<GameObject, bool>? If;

        /// <summary>
        /// What is done each frame while the part is out, with the character and the part.
        /// </summary>
        public Action<GameObject, GameObject>? WhileOut;

        /// <summary>
        /// Whether the part stays out until it is made to go, once read from the local hero's part.
        /// </summary>
        public bool? Lasting;
    }

    /// <summary>
    /// A part that is to come out on a character a while into its clip.
    /// </summary>
    private sealed record Pending(GameObject Character, ushort Id, Rule Rule, string ClipName, float At);

    /// <summary>
    /// A part that is out on a character, with where it is held when it was put somewhere.
    /// </summary>
    private sealed record Showing(GameObject Character, GameObject Part, Rule Rule, Vector3? HeldAt);
}
