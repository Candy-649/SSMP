using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// What the tendrils of a bind of the Witch crest show as they wound creatures (WitchBindEffects, on the hero's 'Bind
/// Effects/Witch Bind'): each heal flashes the hero white and lets out the sparks of a heal ("Witch Pt Heal"). The
/// character of another player showed none of it (USER 11-13 "2吧", asked whether the copy of the tendrils works the
/// heals out by what it touches here, or the player whose bind it is says each one). The player whose bind it is says
/// each heal as their game makes it, and the character here shows it as the game shows it on the hero. The heal itself
/// is their game's alone (USER 11-13 "这种绑定给某个大黄蜂的动作和函数，能不能就本地跑且指向那个大黄蜂").
///
/// So is the white glow that the first wound of each lash lets out, with the stop and the shake of the screen that
/// come with it: a light larger than the whole view, for the screen of that player, which washed over the screen of
/// whoever watched as the glow of the end of a bind did (USER 11-16 "不是的，是很大一片闪光，没有只在角色身上").
/// </summary>
internal class WitchHeal : Bind {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly WitchHeal Instance = new();

    /// <summary>
    /// The binding flags of the members of the game that are looked up.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The sparks of a heal under the hero's 'Bind Effects', which WitchBindEffects lets out at each heal.
    /// </summary>
    private const string HealSparksName = "Witch Pt Heal";

    /// <summary>
    /// The sparks of a heal of WitchBindEffects, with how many it lets out at a time.
    /// </summary>
    private static readonly FieldInfo? HealEffectsField =
        typeof(WitchBindEffects).GetField("healEffects", InstanceFlags);

    /// <summary>
    /// Sends the effect info of a heal of the local hero's tendrils to the other players.
    /// </summary>
    private static Action<byte[]>? _send;

    /// <summary>
    /// The hooks that see the local hero's tendrils heal them, which stay for as long as the game runs.
    /// </summary>
    private static readonly List<ILHook> Hooks = [];

    /// <summary>
    /// Whether the hooks were made, or tried to be.
    /// </summary>
    private static bool _hooked;

    /// <summary>
    /// What the tendrils did, the one byte of the effect info.
    /// </summary>
    private enum Kind : byte {
        /// <summary>
        /// A wound that healed the player.
        /// </summary>
        Heal
    }

    /// <summary>
    /// Starts telling the other players of the heals of the local hero's tendrils. Only the local hero's tendrils have
    /// WitchBindEffects: the copies of the tendrils of other players have it taken off (see <see cref="BindBurst"/>).
    /// </summary>
    /// <param name="send">Sends the effect info to the other players.</param>
    public static void Watch(Action<byte[]> send) {
        _send = send;
        if (_hooked) {
            return;
        }

        _hooked = true;
        AddHook(typeof(WitchBindEffects).GetMethod("OnDamagedEnemy", InstanceFlags), ILHeal);
    }

    /// <summary>
    /// Hooks a method of the game, logging a method that is not there.
    /// </summary>
    private static void AddHook(MethodBase? method, ILContext.Manipulator manipulator) {
        if (method == null) {
            Logger.Error(
                $"Could not find the method for {manipulator.Method.Name}; that part of the Witch crest's bind will " +
                "not show to other players"
            );
            return;
        }

        try {
            Hooks.Add(new ILHook(method, manipulator));
        } catch (Exception e) {
            Logger.Error($"Could not hook the method for {manipulator.Method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// Has a heal of the tendrils say itself, right after the game heals the hero by it.
    /// </summary>
    private static void ILHeal(ILContext il) {
        var c = new ILCursor(il);
        c.GotoNext(MoveType.After, i => i.MatchCallvirt(typeof(HeroController), nameof(HeroController.AddHealth)));
        c.EmitDelegate<Action>(OnHeal);
    }

    /// <summary>
    /// The local hero's tendrils healed them.
    /// </summary>
    private static void OnHeal() {
        Logger.Info($"[Witch] The local hero's tendrils heal them, to {PlayerData.instance.health} masks");
        _send!([(byte) Kind.Heal]);
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not [(byte) Kind.Heal] || HeroController.SilentInstance is not { } hero ||
            !CreateObjects(playerObject, out var bindEffects)) {
            return;
        }

        // The game's own tendrils, on the local hero, which have what the game lets out
        var tendrils = hero.GetComponentInChildren<WitchBindEffects>(true);
        var at = playerObject.transform.position;
        playerObject.GetComponent<SpriteFlash>().flashFocusHeal();
        var heal = (WitchBindEffects.HealEffects) HealEffectsField!.GetValue(tendrils);
        var sparks = CreateEffectIfNotExists(bindEffects, HealSparksName);
        if (sparks != null) {
            sparks.GetComponent<ParticleSystem>().Emit(heal.Emit.GetRandomValue(true));
        }

        Logger.Info($"[Witch] The tendrils of the character at ({at.x:0.0}, {at.y:0.0}) heal its player");
    }
}
