using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The sparks around the hero as they strike the flint, which stay with the hero: they catch as the hero strikes, flare
/// up as the needle takes the element and die down as the hero is done. The hero's handling of the tool tells the
/// thrower's sparks each of those, which the thrower's game sends on to the copy; the copy stays with the thrower's
/// character rather than with this player's hero, and flares up without the warmth that the flare gives the hero, which
/// is the thrower's.
/// </summary>
internal class FlintState : IToolState {
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly FlintState Instance = new();

    /// <summary>
    /// The way the sparks break when the hero is done.
    /// </summary>
    public const byte DieDown = 0;

    /// <summary>
    /// The way the sparks break when they flare up as the needle takes the element.
    /// </summary>
    public const byte FlareUp = 1;

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? HeroField = typeof(FlintUseEffects).GetField("hero", Flags);
    private static readonly FieldInfo? GroupsField = typeof(FlintUseEffects).GetField("effectGroups", Flags);
    private static readonly FieldInfo? CurrentGroupField = typeof(FlintUseEffects).GetField("currentGroup", Flags);
    private static readonly FieldInfo? Pt1Field = typeof(FlintUseEffects).GetField("pt1", Flags);
    private static readonly FieldInfo? Pt2Field = typeof(FlintUseEffects).GetField("pt2", Flags);
    private static readonly MethodInfo? SetGroupMethod = typeof(FlintUseEffects).GetMethod("SetGroup", Flags);
    private static readonly MethodInfo? SetPt1Method = typeof(FlintUseEffects).GetMethod("SetPt1", Flags);
    private static readonly MethodInfo? SetEndMethod = typeof(FlintUseEffects).GetMethod("SetEnd", Flags);

    /// <inheritdoc/>
    public void PrepareCopyPrefab(GameObject copyPrefab) {
    }

    /// <inheritdoc/>
    public void PrepareCopy(GameObject copy, GameObject character, bool poisoned) {
        // The sparks find the hero they stay with as they are set up, and take this player's hero when there is none
        foreach (var effects in copy.GetComponentsInChildren<FlintUseEffects>(true)) {
            HeroField?.SetValue(effects, character.transform);
        }
    }

    /// <inheritdoc/>
    public bool IsSettled(GameObject thing) {
        return true;
    }

    /// <inheritdoc/>
    public byte[] Write(GameObject thing) {
        var effects = thing.GetComponent<FlintUseEffects>();
        var groups = GroupsField?.GetValue(effects) as Array;
        var current = CurrentGroupField?.GetValue(effects);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((sbyte) (groups != null && current != null ? Array.IndexOf(groups, current) : -1));
        writer.Write(Pt1Field?.GetValue(effects) is GameObject { activeSelf: true });
        writer.Flush();
        return stream.ToArray();
    }

    /// <inheritdoc/>
    public void Apply(GameObject copy, byte[] state) {
        using var reader = new BinaryReader(new MemoryStream(state));
        var group = reader.ReadSByte();
        var caught = reader.ReadBoolean();

        if (!copy.TryGetComponent<FlintUseEffects>(out var effects) || group < 0 ||
            CurrentGroupField?.GetValue(effects) != null || SetGroupMethod == null) {
            return;
        }

        var element = SetGroupMethod.GetParameters()[0].ParameterType;
        SetGroupMethod.Invoke(effects, [Enum.ToObject(element, group)]);
        if (caught) {
            SetPt1Method?.Invoke(effects, null);
        }
    }

    /// <inheritdoc/>
    public void Break(GameObject copy, byte how) {
        if (!copy.TryGetComponent<FlintUseEffects>(out var effects)) {
            return;
        }

        if (how == DieDown) {
            SetEndMethod?.Invoke(effects, null);
            return;
        }

        // What the sparks do as they flare up, but for the warmth it gives the local hero
        (Pt1Field?.GetValue(effects) as GameObject)?.SetActive(false);
        (Pt2Field?.GetValue(effects) as GameObject)?.SetActive(true);
        var current = CurrentGroupField?.GetValue(effects);
        if (current == null) {
            return;
        }

        var groupType = current.GetType();
        groupType.GetMethod("StopPt1", Flags)?.Invoke(current, null);
        groupType.GetMethod("StartPt2", Flags)?.Invoke(current, null);
    }
}
