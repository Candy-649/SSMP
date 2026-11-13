using System;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using SSMP.Fsm;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The lash of the down attack of the Witch crest. When its down slash wounds a creature, the game hides the hero, lays
/// a whip from the creature towards them and pulls them in, and shows them again as they bounce off (the hero's 'Crest
/// Attacks' FSM, "NewLash Grab" to "NewLash End"). Other players saw the character glide on in its down slash instead
/// (USER 11-13 "这些动画同步了吗"). The player whose lash it is says where their game laid the whip and which way up it
/// rolled it. The rest runs here on their character, as the game runs it on the hero (USER 11-13 "这种绑定给某个大黄蜂的
/// 动作和函数，能不能就本地跑且指向那个大黄蜂"): the whip turns towards the character as it is seen here, and the
/// character hides for as long as the game hides the hero. Their slash goes on, as it does in the game, which only
/// takes its damage away (NailSlash.CancelAttack hides a slash only by its bounce config, which this one has none of).
/// </summary>
internal class WitchLash : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly WitchLash Instance = new();

    /// <summary>
    /// The hero's FSM of the down attacks of the crests.
    /// </summary>
    private const string FsmName = "Crest Attacks";

    /// <summary>
    /// The state of <see cref="FsmName"/> that lays the whip, hides the hero and starts the pull.
    /// </summary>
    private const string GrabStateName = "NewLash Grab";

    /// <summary>
    /// Where in <see cref="GrabStateName"/> the lash is said: after the whip is laid on the creature, turned and
    /// flipped, before it is switched on.
    /// </summary>
    private const int LaidAt = 8;

    /// <summary>
    /// The whip under the hero, a part that keeps its place in the world while it shows.
    /// </summary>
    private const string WhipPath = "Attacks/Witch/Followup Slash";

    /// <summary>
    /// The name of the whip, which is also the name of the variable of <see cref="FsmName"/> that holds it.
    /// </summary>
    private const string WhipName = "Followup Slash";

    /// <summary>
    /// How long the game hides the hero for: <see cref="GrabStateName"/> sets its "Wait Time" to it, and "NewLash End"
    /// shows the hero again when it has run out, whether the pull stopped at the creature on the way or not.
    /// </summary>
    private static float _lasts;

    /// <summary>
    /// Starts telling the other players of the local hero's lashes. This runs while the hero may still be being made,
    /// like <see cref="HeroChildEffects.Watch"/>.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a lash to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        var fsm = hero.GetComponents<PlayMakerFSM>().FirstOrDefault(component => component.FsmName == FsmName);
        if (fsm == null) {
            Logger.Warn("Unable to find the Crest Attacks FSM to hook.");
            return;
        }

        _lasts = fsm.GetFirstAction<SetFloatValue>(GrabStateName).floatValue.Value;
        var whip = fsm.FsmVariables.FindFsmGameObject(WhipName);
        FsmStateActionInjector.Inject(
            fsm.GetState(GrabStateName), _ => send(Info(whip.Value.transform)), LaidAt, nameof(WitchLash)
        );
    }

    /// <summary>
    /// The effect info of a lash: where the whip lies, and whether it lies upside down.
    /// </summary>
    private static byte[] Info(Transform whip) {
        var info = new byte[9];
        BitConverter.GetBytes(whip.position.x).CopyTo(info, 0);
        BitConverter.GetBytes(whip.position.y).CopyTo(info, 4);
        info[8] = (byte) (whip.localScale.y < 0f ? 1 : 0);
        return info;
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not { Length: 9 } || HeroController.SilentInstance is not { } hero) {
            return;
        }

        var attacks = playerObject.FindGameObjectInChildren("Attacks");
        if (attacks == null) {
            return;
        }

        var whip = attacks.FindGameObjectInChildren(WhipName);
        if (whip == null) {
            var original = hero.transform.Find(WhipPath);
            if (original == null) {
                Logger.Warn("Could not find the whip of the Witch crest's lash on the local hero");
                return;
            }

            whip = HeroCopies.Make(original.gameObject, attacks.transform);
            whip.name = WhipName;
            whip.AddComponent<WitchLashWhip>().Character = playerObject;
        }

        // Laid the way the game lays it, while it is off: on the creature, turned from there towards the character
        // (GetAngleToTarget2D, from 0 to 360) and half round more for a character with its x scale below 0 ("Set
        // Rotation Offset"), and flipped the way the player's game rolled it
        whip.SetActive(false);
        var place = new Vector3(
            BitConverter.ToSingle(effectInfo, 0), BitConverter.ToSingle(effectInfo, 4), whip.transform.position.z
        );
        whip.transform.position = place;

        var towards = playerObject.transform.position - place;
        var angle = Mathf.Atan2(towards.y, towards.x) * Mathf.Rad2Deg;
        if (angle < 0f) {
            angle += 360f;
        }

        if (playerObject.transform.localScale.x < 0f) {
            angle += 180f;
        }

        var rotation = whip.transform.eulerAngles;
        rotation.z = angle;
        whip.transform.eulerAngles = rotation;

        var scale = whip.transform.localScale;
        scale.y = Mathf.Abs(scale.y) * (effectInfo[8] == 1 ? -1f : 1f);
        whip.transform.localScale = scale;

        whip.GetComponent<WitchLashWhip>().EndsAt = Time.time + _lasts;
        whip.SetActive(true);
        HeroChildEffects.ShowBodyUnlessHidden(playerObject);
    }

    /// <summary>
    /// Whether the whip of a lash shows on the character of another player, which the game hides the hero for.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    internal static bool Shows(GameObject playerObject) {
        var attacks = playerObject.FindGameObjectInChildren("Attacks");
        var whip = attacks == null ? null : attacks.FindGameObjectInChildren(WhipName);
        return whip != null && whip.activeSelf;
    }
}

/// <summary>
/// The copy of the whip of a lash on the character of another player. It switches off when the lash is over, as
/// "NewLash End" switches the hero's off, and the character shows again then.
/// </summary>
internal class WitchLashWhip : MonoBehaviour {
    /// <summary>
    /// The character that the whip is on.
    /// </summary>
    [NonSerialized]
    public GameObject? Character;

    /// <summary>
    /// When the lash is over, in the game's time, which the game's own wait counts in too.
    /// </summary>
    [NonSerialized]
    public float EndsAt;

    private void Update() {
        if (Time.time >= EndsAt) {
            gameObject.SetActive(false);
        }
    }

    private void OnDisable() {
        if (Character != null) {
            HeroChildEffects.ShowBodyUnlessHidden(Character);
        }
    }
}
