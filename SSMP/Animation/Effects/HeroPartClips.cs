using System;
using System.IO;
using System.Reflection;
using GlobalSettings;
using MonoMod.RuntimeDetour;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// What the parts of the hero with animations of their own play: the stand-in that the game shows in place of the
/// hero's own sprite for the tools that need a pose of their own, and what goes on top of it. Only the hero's own
/// animation reached the other players, so they saw the hero stand still in whatever it did last while it used such a
/// tool. Switching these parts on and off goes by <see cref="HeroChildEffects"/>, and this plays on the copies what the
/// parts of the local hero play.
/// </summary>
internal class HeroPartClips : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroPartClips Instance = new();

    /// <summary>
    /// The parts, by their path under the hero, which are all parts of <see cref="HeroChildEffects"/> too. Their place in
    /// this list is what they are known by over the network, so new ones go at the end.
    /// </summary>
    private static readonly string[] Paths = [
        "Tool Effects/Tool Hornet",
        "Tool Effects/Tool Hornet Poison Screw Attack",
        "Tool Effects/WebShot Effects/Gun Sprite"
    ];

    /// <summary>
    /// The animators of the parts of the local hero, at the places of their paths.
    /// </summary>
    private static readonly tk2dSpriteAnimator?[] LocalAnimators = new tk2dSpriteAnimator?[Paths.Length];

    /// <summary>
    /// Sends the effect info of a clip that a part of the local hero plays to the other players.
    /// </summary>
    private static Action<byte[]>? _send;

    /// <summary>
    /// The hook of the method that every way of playing a clip ends in, which stays for as long as the game runs.
    /// </summary>
    private static Hook? _playHook;

    /// <summary>
    /// Starts watching the parts of the local hero, which may be a new hero or the same one again. This runs while the
    /// hero may still be being made, like <see cref="HeroChildEffects.Watch"/>.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a clip to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        _send = send;

        var heroTransform = hero.gameObject.transform;
        for (var i = 0; i < Paths.Length; i++) {
            var part = heroTransform.Find(Paths[i]);
            LocalAnimators[i] = part != null ? part.GetComponent<tk2dSpriteAnimator>() : null;
            if (LocalAnimators[i] == null) {
                Logger.Warn($"The hero has no animated part '{Paths[i]}' to show to other players");
            }
        }

        if (_playHook != null) {
            return;
        }

        var method = typeof(tk2dSpriteAnimator).GetMethod(
            "Play",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float)],
            null
        );
        if (method == null) {
            Logger.Error("Could not find the method that plays a clip; the parts of the hero will not animate");
            return;
        }

        _playHook = new Hook(method, OnPlay);
    }

    /// <summary>
    /// The method that every way of playing a clip ends in.
    /// </summary>
    private delegate void PlayMethod(
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    );

    /// <summary>
    /// Plays a clip, and sends it to the other players when a watched part of the local hero plays it.
    /// </summary>
    private static void OnPlay(
        PlayMethod orig,
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        orig(self, clip, clipStartTime, overrideFps);

        if (_send == null || clip == null) {
            return;
        }

        for (var i = 0; i < LocalAnimators.Length; i++) {
            if (!ReferenceEquals(LocalAnimators[i], self)) {
                continue;
            }

            try {
                _send(Write(i, self, clip, clipStartTime, overrideFps));
            } catch (Exception e) {
                Logger.Warn($"Could not send what '{Paths[i]}' of the hero plays: {e.Message}");
            }

            return;
        }
    }

    /// <summary>
    /// Writes a clip that a part plays to effect info: the part, how the clip starts, and the tint of the part, which
    /// goes by the pouch that poisons tools and by the tool that the part belongs to.
    /// </summary>
    private static byte[] Write(
        int index,
        tk2dSpriteAnimator animator,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        var tint = animator.GetComponent<PoisonTintBase>();
        var tintTool = tint != null && tint.ReadFromTool != null ? tint.ReadFromTool.name : "";

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) index);
        writer.Write(clip.name);
        writer.Write(clipStartTime);
        writer.Write(overrideFps);
        writer.Write(Gameplay.PoisonPouchTool.IsEquipped);
        writer.Write(tintTool);
        writer.Flush();
        return stream.ToArray();
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null) {
            return;
        }

        int index;
        string clipName;
        float clipStartTime;
        float overrideFps;
        bool poisoned;
        string tintToolName;
        try {
            using var reader = new BinaryReader(new MemoryStream(effectInfo));
            index = reader.ReadByte();
            clipName = reader.ReadString();
            clipStartTime = reader.ReadSingle();
            overrideFps = reader.ReadSingle();
            poisoned = reader.ReadBoolean();
            tintToolName = reader.ReadString();
        } catch (IOException) {
            return;
        }

        if (index >= Paths.Length) {
            return;
        }

        var part = HeroChildEffects.GetCopy(playerObject, Paths[index], true);
        if (part == null || !part.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
            return;
        }

        var tintTool = tintToolName.Length > 0 ? ToolItemManager.GetToolByName(tintToolName) as ToolItem : null;
        foreach (var tint in part.GetComponentsInChildren<PoisonTintBase>(true)) {
            if (tintTool != null) {
                tint.ReadFromTool = tintTool;
            }

            tint.SetPoisoned(poisoned);
        }

        var clip = animator.GetClipByName(clipName);
        if (clip == null) {
            Logger.Warn($"The part '{Paths[index]}' has no clip '{clipName}' to play");
            return;
        }

        animator.Play(clip, clipStartTime, overrideFps);
    }
}
