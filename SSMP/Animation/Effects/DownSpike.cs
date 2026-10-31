using SSMP.Internals;
using SSMP.Networking.Packet;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// Abstract base class for the animation effect of down spikes.
/// </summary>
internal class DownSpike : SlashBase {
    /// <summary>
    /// The drill of the fully charged down attack of the Architect crest, which is a drill of its own: the hero's
    /// 'Crest Attacks' FSM starts 'Attacks/Toolmaster/DownSlash New Charged' for it (in 'Drill Charged') and
    /// 'DownSlash New' for the uncharged one (in 'Drill Uncharged').
    /// </summary>
    internal const string ChargedDrillName = "DownSlash New Charged";

    /// <summary>
    /// The drill of the uncharged down attack of the Architect crest (see <see cref="ChargedDrillName"/>).
    /// </summary>
    internal const string DrillName = "DownSlash New";

    /// <summary>
    /// Whether this is the fully charged down attack, which the Architect crest drills with its own drill.
    /// </summary>
    private readonly bool _charged;

    /// <param name="charged">Whether this is the fully charged down attack.</param>
    public DownSpike(bool charged = false) {
        _charged = charged;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length < 1) {
            Logger.Error("Could not get null or empty effect info for SlashBase");
            return;
        }

        var packet = new Packet(effectInfo);
        var slashEffects = packet.ReadBitFlag<SlashEffect>();

        var toolCrest = ToolItemManager.GetCrestByName(crestType.ToInternal());
        if (toolCrest == null) {
            Logger.Error($"Could not find unknown ToolCrest with type: {crestType}, {crestType.ToInternal()}");
            return;
        }

        if (crestType is CrestType.Witch or CrestType.Architect) {
            Play(playerObject, SlashType.Down, crestType, slashEffects);
        } else {
            Play(playerObject, SlashType.DownSpike, crestType, slashEffects);
        }
    }

    /// <inheritdoc/>
    protected override NailAttackBase? GetNailAttackBase(
        SlashType type,
        CrestType crestType,
        bool isInBeastRageMode,
        HeroController.ConfigGroup configGroup,
        HeroController.ConfigGroup? overrideGroup
    ) {
        // The charged one was shown with the drill of the uncharged one
        if (_charged && crestType == CrestType.Architect) {
            return GetNailSlashInCrestObjectFromName(configGroup, ChargedDrillName);
        }

        return base.GetNailAttackBase(type, crestType, isInBeastRageMode, configGroup, overrideGroup);
    }

    /// <inheritdoc/>
    protected override void HandleSlashSpriteAnimation(
        tk2dSpriteAnimator anim, 
        PolygonCollider2D poly, 
        MeshRenderer mesh, 
        CrestType crestType,
        GameObject slashParent, 
        string animName
    ) {
        if (crestType is CrestType.Witch or CrestType.Architect) {
            base.HandleSlashSpriteAnimation(anim, poly, mesh, crestType, slashParent, animName);
            return;
        }
        
        poly.enabled = true;

        // When the animation completes we are disabling the components and destroying the slash parent
        // Not sure if this is the moment this should happen
        anim.AnimationCompleted = (_, _) => {
            poly.enabled = false;
            mesh.enabled = false;
            anim.AnimationEventTriggered = null;
            
            Object.Destroy(slashParent);
        };

        anim.PlayFromFrame(animName, 0);
    }
}
