using System;
using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// Draws the cloak of the Hornet who sits on the settings page in the colour chosen for it in Player Color, which
/// colours only the hero of a save, and so nothing in the menu where the colour is chosen. It is on the camera that
/// draws her: it puts a material of Player Color's shader on her just before that camera draws her and her own back
/// after, as her animation puts her own on her whenever her picture changes, after everything else of the frame ran.
/// A skin keeps its own colours, as in Player Color: only the game's own art has masks.
/// </summary>
internal sealed class CloakColourPreview : MonoBehaviour {
    /// <summary>
    /// Her sprite.
    /// </summary>
    [NonSerialized]
    private tk2dSprite? _sprite;

    /// <summary>
    /// What draws her.
    /// </summary>
    [NonSerialized]
    private MeshRenderer? _renderer;

    /// <summary>
    /// The colour chosen, or null while the cloak keeps its own.
    /// </summary>
    [NonSerialized]
    private PlayerColorLook? _look;

    /// <summary>
    /// What stops watching the settings of Player Color, or null while nothing watches them.
    /// </summary>
    [NonSerialized]
    private Action? _stopWatching;

    /// <summary>
    /// The material that draws each atlas in the colour, with the mask of its cloak, by the game's own texture of the
    /// atlas; null for an atlas that has no cloak to colour.
    /// </summary>
    [NonSerialized]
    private readonly Dictionary<Texture, (Material Material, Texture Mask)?> _colored = new();

    /// <summary>
    /// Her own material, to put back once the camera drew her in the colour, or null if it drew her in her own.
    /// </summary>
    [NonSerialized]
    private Material? _own;

    /// <summary>
    /// Has the given camera draw the given figure in the colour chosen.
    /// </summary>
    public static void Attach(Camera camera, GameObject figure) {
        var preview = camera.gameObject.AddComponent<CloakColourPreview>();
        preview._sprite = figure.GetComponent<tk2dSprite>();
        preview._renderer = figure.GetComponent<MeshRenderer>();
    }

    private void OnEnable() {
        _look = PlayerColorMod.LocalLook();
        _stopWatching ??= PlayerColorMod.OnSettingsChanged(() => _look = PlayerColorMod.LocalLook());
    }

    private void OnDisable() {
        _stopWatching?.Invoke();
        _stopWatching = null;
    }

    private void OnDestroy() {
        foreach (var colored in _colored.Values) {
            if (colored is { Material: var material } && material != null) {
                Destroy(material);
            }
        }

        _colored.Clear();
    }

    private void OnPreCull() {
        _own = null;
        if (_look == null || _renderer == null || _sprite == null || _sprite.Collection == null) {
            return;
        }

        var own = _renderer.sharedMaterial;
        var texture = own != null ? own.mainTexture : null;
        if (own == null || texture == null || !OwnSkin.IsGameTexture(texture)) {
            return;
        }

        if (!_colored.TryGetValue(texture, out var colored)) {
            colored = null;
            var shader = PlayerColorMod.Shader;
            var mask = PlayerColorMod.MaskFor(_sprite.Collection.name, texture.name);
            if (shader != null && mask != PlayerColorMod.NoMask) {
                colored = (new Material(shader) { name = "SSMP cloak colour " + texture.name }, mask);
            }

            _colored[texture] = colored;
        }

        if (colored is not { Material: var material, Mask: var cloak } || material == null) {
            return;
        }

        PlayerColorMod.Paint(material, texture, cloak, own, _look);
        // Drawn in her own queue, which her outline is drawn in to stay behind her
        material.renderQueue = own.renderQueue;
        _own = own;
        _renderer.sharedMaterial = material;
    }

    private void OnPostRender() {
        if (_own != null && _renderer != null) {
            _renderer.sharedMaterial = _own;
        }

        _own = null;
    }
}
