using System.Collections.Generic;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The needles that come out on both sides of a player as they charge a super jump, and go up as they throw. The game
/// shows them by two parts of the hero that it switches on as the charge begins and off only once the whole jump is
/// over: as the charge begins it plays them a clip that loops for as long as it lasts, and as the jump is let go one
/// that ends with them gone. Copied part by part, with what each part played sent apart from what the character itself
/// did, the needles went up on the other players' screens and then showed out at the sides again for the rest of the
/// jump (USER 10-31 "那个针是先演了上去的动画结果又闪回了之前的位置").
///
/// Nothing of them goes over the network now. They go by the two clips of the character that a super jump is made of,
/// which reach the other players like everything else the character does (USER 10-31 "超级跳只需要两个消息，蓄力和发射。
/// 其他的动画都绑定在这两个消息上"): as the character begins to charge, the needles come out on it; as it throws, they
/// play their end and are switched off once it is over; as it starts anything else while they are out, the charge was
/// let go too soon or broken off, and they go at once, as the game switches them off then.
/// </summary>
internal static class SuperJumpNeedles {
    /// <summary>
    /// The needles, by their path under the hero. <see cref="HeroChildEffects"/> keeps their places in its list, so
    /// that their copies go when a character leaves the room, but neither watches nor sends them.
    /// </summary>
    public static readonly string[] Paths = [
        "Effects/Super Jump Antic Effect L",
        "Effects/Super Jump Antic Effect R"
    ];

    /// <summary>
    /// The clip of the character as it begins to charge.
    /// </summary>
    private const string ChargeClip = "Super Jump Antic";

    /// <summary>
    /// The clip of the character as it throws the needle that pulls it up.
    /// </summary>
    private const string ThrowClip = "Super Jump Throw";

    /// <summary>
    /// The clip of the needles while they are out, which loops.
    /// </summary>
    private const string OutClip = "Super Jump Antic Effect";

    /// <summary>
    /// The clip of the needles as they go up, which plays once and ends with them gone.
    /// </summary>
    private const string UpClip = "Super Jump Antic Effect End";

    /// <summary>
    /// The characters whose needles show, out or going up, so that the clips of every other character are passed
    /// over at once.
    /// </summary>
    private static readonly HashSet<GameObject> Showing = [];

    /// <summary>
    /// Brings the needles of the character of another player out, or makes them go, by the clip the character starts.
    /// </summary>
    /// <param name="playerObject">The character.</param>
    /// <param name="id">The ID of the player, for the log.</param>
    /// <param name="clipName">The clip that the character starts.</param>
    public static void OnCharacterClip(GameObject playerObject, ushort id, string clipName) {
        if (clipName == ChargeClip) {
            ComeOut(playerObject, id);
            return;
        }

        if (!Showing.Contains(playerObject)) {
            return;
        }

        var stillShowing = false;
        var goingUp = false;
        var gone = false;
        foreach (var path in Paths) {
            var needle = HeroChildEffects.GetCopy(playerObject, path, false);
            if (needle == null || !needle.activeSelf || !needle.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
                continue;
            }

            // Going up already, which they finish whatever the character does next, as in the game
            if (animator.CurrentClip != null && animator.CurrentClip.name == UpClip) {
                stillShowing = true;
                continue;
            }

            var upClip = clipName == ThrowClip ? animator.GetClipByName(UpClip) : null;
            if (upClip == null) {
                needle.SetActive(false);
                gone = true;
                continue;
            }

            animator.AnimationCompleted = OnClipEnded;
            animator.Play(upClip);
            goingUp = true;
            stillShowing = true;
        }

        if (!stillShowing) {
            Showing.Remove(playerObject);
        }

        if (goingUp) {
            Logger.Info($"[Needles] Player {id} throws the needle of a super jump: its needles go up and go");
        } else if (gone) {
            Logger.Info(
                $"[Needles] Player {id} starts '{clipName}' out of the charge of a super jump: its needles go at once"
            );
        }
    }

    /// <summary>
    /// Forgets the needles of a character that leaves the room, whose copies are switched off with the other copied
    /// parts of the hero (see <see cref="HeroChildEffects.ResetCopies"/>).
    /// </summary>
    /// <param name="playerObject">The character.</param>
    public static void Reset(GameObject playerObject) {
        Showing.Remove(playerObject);
    }

    /// <summary>
    /// Brings the needles of a character out from the start of their clip, unless they are out already for this charge.
    /// </summary>
    private static void ComeOut(GameObject playerObject, ushort id) {
        var shown = false;
        var already = true;
        foreach (var path in Paths) {
            var needle = HeroChildEffects.GetCopy(playerObject, path, true);
            if (needle == null || !needle.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
                continue;
            }

            var outClip = animator.GetClipByName(OutClip);
            if (outClip == null) {
                needle.SetActive(false);
                continue;
            }

            shown = true;

            // The clip of the charge told again: the needles go on as they are
            if (needle.activeSelf && animator.IsPlaying(outClip)) {
                continue;
            }

            already = false;
            needle.SetActive(true);
            animator.AnimationCompleted = OnClipEnded;
            animator.PlayFromFrame(outClip, 0);
        }

        if (!shown) {
            Logger.Info($"[Needles] Player {id} charges a super jump, but its character has no needles to show");
            return;
        }

        Showing.Add(playerObject);
        if (!already) {
            Logger.Info($"[Needles] Player {id} charges a super jump: its needles come out");
        }
    }

    /// <summary>
    /// Switches a needle off as it has gone up, as it would be gone in the game until the jump is over.
    /// </summary>
    private static void OnClipEnded(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip) {
        if (clip != null && clip.name == UpClip) {
            animator.gameObject.SetActive(false);
        }
    }
}
