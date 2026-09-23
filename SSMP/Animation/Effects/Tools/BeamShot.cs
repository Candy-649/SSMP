using System;
using System.Collections;
using System.IO;
using GlobalSettings;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The shot that goes from the hero to where it strikes at once, the first wall ahead or the edge of the view: a part
/// of the hero that is put where the shot strikes, with a trail back to the hero that hits whatever it crosses, and
/// strands that fly out along it. Where it strikes is worked out from the thrower's view of the room, so it goes from
/// the thrower's game, and the copy on the thrower's character only shows the shot and hits as the partner's attacks
/// do.
/// </summary>
internal static class BeamShot {
    /// <summary>
    /// The part of the hero that is put where the shot strikes, with the trail back to the hero under it.
    /// </summary>
    private const string ImpactPath = "Tool Effects/WebShot Effects/SnipeShot Impact";

    /// <summary>
    /// The name of the trail under the part where the shot strikes, which is stretched to the hero.
    /// </summary>
    private const string TrailName = "SnipeShot Trail";

    /// <summary>
    /// The part of the hero with the strands that fly out along the shot.
    /// </summary>
    private const string StrandsPath = "Tool Effects/WebShot Effects/Pt SnipeStrands";

    /// <summary>
    /// The name of the part of the strands that shows for a thrower with the pouch that poisons their tools.
    /// </summary>
    private const string PoisonTrailName = "Pt Poison Trail";

    /// <summary>
    /// How far in front of the rest the strands are drawn, as the game puts them.
    /// </summary>
    private const float StrandsZ = 0.003f;

    /// <summary>
    /// The seconds that the strands take to fly out to where the shot strikes.
    /// </summary>
    private const float StrandsTime = 0.08f;

    /// <summary>
    /// Starts watching the local hero for the shot.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends a message about a shot to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        // The transform of the object, not HeroController.transform, which is not set yet this early
        var impact = hero.gameObject.transform.Find(ImpactPath);
        if (impact == null) {
            Logger.Warn($"The hero has no part '{ImpactPath}' to show to other players");
            return;
        }

        impact.gameObject.AddComponentIfNotPresent<BeamShotWatcher>().Fired = () => send(Write(impact));
    }

    /// <summary>
    /// Writes the message about a shot, as the game switches on the part where it strikes: where that is, and how far
    /// the trail reaches back to the hero.
    /// </summary>
    private static byte[] Write(Transform impact) {
        var trail = impact.Find(TrailName);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) ToolMessageKind.Beam);
        writer.Write((byte) 0);
        writer.Write(impact.position.x);
        writer.Write(impact.position.y);
        writer.Write(trail != null ? trail.localScale.x : 1f);
        writer.Write(Gameplay.PoisonPouchTool.IsEquipped);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Shows a shot of another player on their character.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    /// <param name="reader">The message, from after its kind and number.</param>
    public static void Play(GameObject playerObject, BinaryReader reader) {
        var point = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var length = reader.ReadSingle();
        var poisoned = reader.ReadBoolean();

        var impact = HeroChildEffects.GetCopy(playerObject, ImpactPath, true);
        if (impact != null) {
            ThrownTool.MarkRemote(impact);

            // Placed while it is off, as the part keeps the place it is switched on at while the hero moves on
            impact.SetActive(false);
            var transform = impact.transform;
            transform.position = new Vector3(point.x, point.y, transform.position.z);
            var trail = transform.Find(TrailName);
            if (trail != null) {
                var scale = trail.localScale;
                trail.localScale = new Vector3(length, scale.y, scale.z);
            }

            impact.SetActive(true);
            foreach (var tint in impact.GetComponentsInChildren<PoisonTintBase>(true)) {
                tint.SetPoisoned(poisoned);
            }
        }

        var strands = HeroChildEffects.GetCopy(playerObject, StrandsPath, true);
        if (strands == null) {
            return;
        }

        strands.SetActive(true);
        var poisonTrail = strands.transform.Find(PoisonTrailName);
        if (poisonTrail != null) {
            poisonTrail.gameObject.SetActive(poisoned);
        }

        var start = (Vector2) playerObject.transform.position;
        strands.transform.position = new Vector3(start.x, start.y, StrandsZ);
        if (strands.TryGetComponent<ParticleSystem>(out var particles)) {
            particles.Play();
        }

        MonoBehaviourUtil.Instance.StartCoroutine(FlyOut(strands.transform, start, point));
    }

    /// <summary>
    /// Moves the strands from the character out to where the shot strikes, as the game moves them.
    /// </summary>
    private static IEnumerator FlyOut(Transform strands, Vector2 from, Vector2 to) {
        for (var elapsed = 0f; elapsed < StrandsTime; elapsed += Time.deltaTime) {
            if (strands == null) {
                yield break;
            }

            var at = Vector2.Lerp(from, to, elapsed / StrandsTime);
            strands.position = new Vector3(at.x, at.y, strands.position.z);
            yield return null;
        }

        if (strands != null) {
            strands.position = new Vector3(to.x, to.y, strands.position.z);
        }
    }
}

/// <summary>
/// Watches the part of the local hero where the shot of <see cref="BeamShot"/> strikes for being switched on.
/// </summary>
internal class BeamShotWatcher : MonoBehaviour {
    /// <summary>
    /// Called when the part is switched on. Not serialized, so the copy of the part on another player's character,
    /// which has this component too, watches nothing.
    /// </summary>
    [NonSerialized]
    public Action? Fired;

    private void OnEnable() {
        Fired?.Invoke();
    }
}
