using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The glow of the lantern that a player wears: the haze around them and the faint wisps that drift about them, shown
/// on their character. Only the look goes. The character has none of the lantern itself, which looks for enemies near
/// the hero and sends its wisps at them; the copies of those come from the wearer's game as the other things of their
/// tools do (see <see cref="ToolCopyRules.LanternSpawns"/>). The wearer's game sends when the glow goes on and off, and
/// its list of what is out says it again every second.
/// </summary>
internal static class WispLantern {
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The haze around the hero and the wisps that drift about them, of the lantern (HeroWispLantern), which it
    /// switches on and off together.
    /// </summary>
    private static readonly FieldInfo? HazeField = typeof(HeroWispLantern).GetField("haze", Flags);

    /// <inheritdoc cref="HazeField" />
    private static readonly FieldInfo? DriftField = typeof(HeroWispLantern).GetField("idlePt", Flags);

    /// <summary>
    /// The characters of the players whose glow shows here, by their instance ID.
    /// </summary>
    private static readonly HashSet<int> Glowing = [];

    /// <summary>
    /// Where the haze and the drifting wisps are under the hero, and whether the drifting wisps are switched on there,
    /// which the lantern only plays and stops: read from the local hero's lantern the first time, the same on every
    /// hero, so that a glow is taken away from a character also when there is no local hero.
    /// </summary>
    private static (string Haze, string Drift, bool DriftOn)? _parts;

    /// <summary>
    /// Writes the message that the glow of the local hero's lantern went on or off.
    /// </summary>
    public static byte[] Write(bool on) {
        return [(byte) ToolMessageKind.Lantern, 0, (byte) (on ? 1 : 0)];
    }

    /// <summary>
    /// Shows the glow of another player's lantern on their character, or takes it away, if that is not what shows
    /// already: as their game says it went on or off, and as their list of what is out says it again, for a message
    /// about it that got lost or a character that came into the room later.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    /// <param name="on">Whether their glow shows.</param>
    public static void Keep(GameObject playerObject, bool on) {
        if (Glowing.Contains(playerObject.GetInstanceID()) != on) {
            Show(playerObject, on);
        }
    }

    /// <summary>
    /// Takes the glow away from the character of another player who leaves the room, which may come back, or be used
    /// for another player.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    public static void Stop(GameObject playerObject) {
        Keep(playerObject, false);
    }

    /// <summary>
    /// Switches the glow on the character of another player the way the lantern switches its own
    /// (HeroWispLantern.ToggleLanternEffects), on copies of the local hero's haze and drifting wisps at the same places
    /// under the character.
    /// </summary>
    private static void Show(GameObject playerObject, bool on) {
        if (PartsOfTheGlow() is not { } parts) {
            Logger.Warn(
                $"[Tools] There is no lantern on the local hero to show the glow of '{playerObject.name}' with"
            );
            return;
        }

        var hazeCopy = HeroChildEffects.GetCopy(playerObject, parts.Haze, on);
        if (hazeCopy != null) {
            hazeCopy.SetActive(on);
        }

        // A copy is made switched off, and is on as the local hero's is
        var driftCopy = HeroChildEffects.GetCopy(playerObject, parts.Drift, on);
        if (driftCopy != null && driftCopy.TryGetComponent<ParticleSystem>(out var particles)) {
            if (on) {
                driftCopy.SetActive(parts.DriftOn);
                particles.Play(true);
            } else {
                particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        if (on) {
            Glowing.Add(playerObject.GetInstanceID());
        } else {
            Glowing.Remove(playerObject.GetInstanceID());
        }

        Logger.Info($"[Tools] The wisp lantern glow of '{playerObject.name}' goes {(on ? "on" : "off")}");
    }

    /// <summary>
    /// Where the haze and the drifting wisps are under the hero (see <see cref="_parts"/>), or null while that was
    /// never read and there is no local hero with a lantern to read it from.
    /// </summary>
    private static (string Haze, string Drift, bool DriftOn)? PartsOfTheGlow() {
        if (_parts != null) {
            return _parts;
        }

        var hero = HeroController.SilentInstance;
        var lantern = hero != null ? hero.GetComponentInChildren<HeroWispLantern>(true) : null;
        if (hero == null || lantern == null || HazeField?.GetValue(lantern) is not GameObject haze ||
            DriftField?.GetValue(lantern) is not ParticleSystem drift) {
            return null;
        }

        _parts = (PathOf(haze.transform, hero.transform), PathOf(drift.transform, hero.transform),
            drift.gameObject.activeSelf);
        return _parts;
    }

    /// <summary>
    /// The path of a part of the hero under the hero.
    /// </summary>
    private static string PathOf(Transform part, Transform hero) {
        var path = part.name;
        for (var step = part.parent; step != null && step != hero; step = step.parent) {
            path = $"{step.name}/{path}";
        }

        return path;
    }
}
