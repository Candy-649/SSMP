using System;
using System.Text;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Writes down every hurt of the local player: what hit them, whether it could be seen, and where. A player was hit by
/// what looked like thin air coming down from a ledge (USER 10-10, "朋友被空气打到"), and nothing in either log could
/// say what reached them, because only deaths were written down.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The most hurts that are written down in one minute, so that something hurting the player over and over cannot
    /// fill the log.
    /// </summary>
    private const int HurtLinesPerMinute = 30;

    /// <summary>
    /// When the current minute of written-down hurts began, in unscaled seconds.
    /// </summary>
    private static float _hurtMinuteStart;

    /// <summary>
    /// How many hurts were written down in the current minute.
    /// </summary>
    private static int _hurtLinesThisMinute;

    /// <summary>
    /// Writes down a hurt of the local player that took health, once the game is done with it.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="source">What hit the player, as the game was given it.</param>
    /// <param name="damageAmount">The damage the game was asked to deal.</param>
    /// <param name="hazardType">The kind of harm.</param>
    /// <param name="flags">What else the damage was marked with.</param>
    /// <param name="healthBefore">The health of the player before the hurt.</param>
    private static void NoteHurt(
        HeroController hero,
        GameObject? source,
        int damageAmount,
        GlobalEnums.HazardType hazardType,
        GlobalEnums.DamagePropertyFlags flags,
        int healthBefore
    ) {
        try {
            var playerData = PlayerData.instance;
            if (playerData == null) {
                return;
            }

            // A hit that took nothing - the player was safe from it for the moment - is not a hurt
            var healthAfter = playerData.health;
            if (healthAfter >= healthBefore) {
                return;
            }

            var now = Time.unscaledTime;
            if (now - _hurtMinuteStart >= 60f) {
                _hurtMinuteStart = now;
                _hurtLinesThisMinute = 0;
            }

            if (_hurtLinesThisMinute++ >= HurtLinesPerMinute) {
                return;
            }

            var line = new StringBuilder();
            line.Append($"[Hurts] {healthBefore} -> {healthAfter}/{playerData.maxHealth} from {damageAmount} damage");
            line.Append($" ({hazardType}{(((int) flags & 2) != 0 ? ", not lethal" : "")})");

            var heroPosition = hero.transform.position;
            if (source == null) {
                line.Append(", with nothing named as its source");
            } else {
                var sourcePosition = source.transform.position;
                line.Append($" by '{PathOf(source.transform)}'");
                line.Append(source.activeInHierarchy ? "" : " (switched off)");
                line.Append($", {DescribeDrawn(source)} on it");

                // The creature it belongs to: the nearest object above it that can be hurt itself
                var creature = source.GetComponentInParent<HealthManager>(true);
                if (creature != null && creature.gameObject != source) {
                    line.Append($", part of '{creature.name}' ({DescribeDrawn(creature.gameObject)})");
                }

                var copied = Entity.Entity.FindByCopyPart(source);
                line.Append(copied != null
                    ? $", which is this game's copy of entity {copied.Id} ({copied.Type})"
                    : ", which is this game's own");
                line.Append(
                    $", {Vector2.Distance(sourcePosition, heroPosition):0.0} away at " +
                    $"({sourcePosition.x:0.00}, {sourcePosition.y:0.00})"
                );

                // Which way it faces and goes, and which side of it the player is on: a creature that charged a
                // player back first (USER 10-10, "飞天怪用屁股撞") shows here as one going towards the player while its
                // x scale says it faces the other way
                var body = creature != null ? creature.gameObject : source;
                line.Append($", {DescribeFacing(body, copied, heroPosition)}");
            }

            line.Append($"; the player at ({heroPosition.x:0.00}, {heroPosition.y:0.00})");
            Logger.Info(line.ToString());
        } catch (Exception e) {
            Logger.Warn($"Could not write down a hurt of the player: {e.Message}");
        }
    }

    /// <summary>
    /// Which way a creature faces (the sign of its x scale) and goes, which side of it the player is on, and for a copy
    /// what the game that runs it last said of which way it faces.
    /// </summary>
    /// <param name="body">The creature.</param>
    /// <param name="copied">The entity whose copy it is, or null for this game's own.</param>
    /// <param name="heroPosition">Where the player is.</param>
    private static string DescribeFacing(GameObject body, Entity.Entity? copied, Vector3 heroPosition) {
        var transform = body.transform;
        var text = new StringBuilder($"x scale {(transform.lossyScale.x >= 0f ? "+" : "-")}");
        Vector3? velocity = null;
        if (copied != null) {
            velocity = copied.CopyVelocity;
        } else if (body.TryGetComponent<Rigidbody2D>(out var rigidbody)) {
            velocity = rigidbody.linearVelocity;
        }

        if (velocity is { } going) {
            text.Append($" going ({going.x:0.0}, {going.y:0.0})");
        }

        text.Append($", the player on its {(heroPosition.x >= transform.position.x ? "right" : "left")}");
        if (copied != null) {
            text.Append($", {copied.DescribeToldFacing()}");
        }

        return text.ToString();
    }

    /// <summary>
    /// How much of an object is drawn: of the sprites and meshes switched on under it, how many the camera draws.
    /// </summary>
    private static string DescribeDrawn(GameObject root) {
        var drawn = 0;
        var shown = 0;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
            if (renderer is ParticleSystemRenderer || !renderer.enabled) {
                continue;
            }

            shown++;
            if (renderer.isVisible && !IsSeeThrough(renderer)) {
                drawn++;
            }
        }

        return shown == 0 ? "nothing to draw" : $"{drawn} of {shown} drawn";
    }

    /// <summary>
    /// Whether a sprite is drawn fully see-through.
    /// </summary>
    private static bool IsSeeThrough(Renderer renderer) {
        return renderer.TryGetComponent<tk2dBaseSprite>(out var sprite) ? sprite.color.a <= 0.01f
            : renderer is SpriteRenderer spriteRenderer && spriteRenderer.color.a <= 0.01f;
    }
}
