using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// An outline drawn from copies of a sprite, for the things that mark this player's own: their head on the map
/// (<see cref="OwnMapIconOutline"/>) and their figure in the room (<see cref="OwnFigureOutline"/>).
///
/// Each copy draws the sprite's own mesh in one flat colour, moved a little to one side, behind the sprite. The copies
/// share the sprite's mesh, which the game rebuilds in place when the sprite's animation changes frame, so the outline
/// keeps to the sprite's shape. Only the alpha of the sprite's texture is drawn, so whatever the sprite holds that is
/// not quite clear, like the soft glow around the head on the map, is drawn in the colour as well.
/// </summary>
internal abstract class SpriteOutline : MonoBehaviour {
    /// <summary>
    /// The shader that draws a mesh in one flat colour with only its texture's alpha: Unity's own text shader, which
    /// the game ships in its default resources.
    /// </summary>
    private const string FlatShaderName = "GUI/Text Shader";

    /// <summary>
    /// How many directions the copies are moved in.
    /// </summary>
    private const int Directions = 12;

    /// <summary>
    /// Where the inner copies sit, as a part of the reach. Copies only at the full reach would leave gaps beside thin
    /// parts of the sprite, such as the horns of the head.
    /// </summary>
    private const float InnerReach = 0.6f;

    /// <summary>
    /// The flat shader, once found.
    /// </summary>
    private static Shader? _flatShader;

    /// <summary>
    /// Whether the flat shader was looked for and is not there, so that it is looked for and reported once.
    /// </summary>
    private static bool _flatShaderMissing;

    /// <summary>
    /// The copies of the sprite, each with the direction it is moved in, how far, and its sorting order from the
    /// sprite's. In the same order as <see cref="CopyRenderers"/>.
    /// </summary>
    private readonly List<(MeshFilter Filter, Vector2 Direction, float Reach, int Order)> _copies = [];

    /// <summary>
    /// The materials the copies are drawn with, which take the sprite's texture.
    /// </summary>
    private readonly List<Material> _materials = [];

    /// <summary>
    /// The mesh filter of the sprite, which holds the mesh the copies share.
    /// </summary>
    private MeshFilter? _spriteFilter;

    /// <summary>
    /// The mesh the copies were last given.
    /// </summary>
    private Mesh? _mesh;

    /// <summary>
    /// The material of the sprite the copies last took the texture of.
    /// </summary>
    private Material? _spriteMaterial;

    /// <summary>
    /// The sorting layer and order of the sprite that the copies were last drawn by.
    /// </summary>
    private (int Layer, int Order)? _sorting;

    /// <summary>
    /// The renderer of the sprite, whose texture the copies draw with.
    /// </summary>
    protected Renderer? SpriteRenderer { get; private set; }

    /// <summary>
    /// The renderers of the copies.
    /// </summary>
    protected List<Renderer> CopyRenderers { get; } = [];

    /// <summary>
    /// Find the flat shader, or report once that it is not there.
    /// </summary>
    /// <param name="what">What would have had the outline, for the report.</param>
    /// <returns>The shader, or null if it is not there.</returns>
    protected static Shader? FindFlatShader(string what) {
        if (_flatShader == null && !_flatShaderMissing) {
            _flatShader = Shader.Find(FlatShaderName);

            if (_flatShader == null) {
                _flatShaderMissing = true;
                Logger.Warn($"Shader \"{FlatShaderName}\" not found, {what} gets no outline");
            }
        }

        return _flatShader;
    }

    /// <summary>
    /// Remove anything left on a sprite by an outline of the given name that was not finished, so that a failure does
    /// not add one more every time.
    /// </summary>
    /// <param name="sprite">The sprite.</param>
    /// <param name="objectName">The name of the object that holds the outline.</param>
    protected static void RemoveLeftOver(GameObject sprite, string objectName) {
        var left = sprite.transform.Find(objectName);
        if (left != null) {
            left.gameObject.SetActive(false);
            Destroy(left.gameObject);
        }
    }

    /// <summary>
    /// Take the mesh filter and renderer of the sprite to outline.
    /// </summary>
    /// <param name="sprite">The sprite.</param>
    /// <returns>Whether the sprite has both.</returns>
    protected bool TakeSprite(GameObject sprite) {
        if (!sprite.TryGetComponent<MeshFilter>(out var filter) ||
            !sprite.TryGetComponent<Renderer>(out var renderer)) {
            return false;
        }

        _spriteFilter = filter;
        SpriteRenderer = renderer;
        return true;
    }

    /// <summary>
    /// Add the copies of one ring.
    /// </summary>
    /// <param name="material">The material the ring is drawn with.</param>
    /// <param name="order">The sorting order the ring is drawn at, from the sprite's: 0 at the sprite's own, -1 just
    /// under it.</param>
    /// <param name="reach">How far the ring reaches out from the sprite, as <see cref="CopyPosition"/> takes it.
    /// </param>
    protected void AddRing(Material material, int order, float reach) {
        if (!_materials.Contains(material)) {
            _materials.Add(material);
        }

        foreach (var part in new[] { InnerReach, 1f }) {
            for (var i = 0; i < Directions; i++) {
                var angle = i * 2f * Mathf.PI / Directions;

                var copy = new GameObject("Outline Copy") { layer = gameObject.layer };
                copy.transform.SetParent(transform, false);

                var filter = copy.AddComponent<MeshFilter>();
                var renderer = copy.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.sortingLayerID = SpriteRenderer!.sortingLayerID;
                renderer.sortingOrder = SpriteRenderer.sortingOrder + order;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                _copies.Add((filter, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), reach * part, order));
                CopyRenderers.Add(renderer);
            }
        }

        _sorting = (SpriteRenderer.sortingLayerID, SpriteRenderer.sortingOrder);
    }

    /// <summary>
    /// Where a copy goes, under the object that holds the outline.
    /// </summary>
    /// <param name="direction">The direction the copy is moved in.</param>
    /// <param name="reach">How far it is moved, as given to <see cref="AddRing"/>.</param>
    /// <param name="mesh">The mesh of the sprite, or null if it has none yet.</param>
    /// <returns>The local position of the copy.</returns>
    protected abstract Vector3 CopyPosition(Vector2 direction, float reach, Mesh? mesh);

    /// <summary>
    /// Called when the copies take the texture of another material of the sprite, which a sprite whose frames lie on
    /// more than one texture changes between.
    /// </summary>
    /// <param name="spriteMaterial">The material of the sprite.</param>
    protected virtual void OnSpriteMaterialChanged(Material spriteMaterial) {
    }

    /// <summary>
    /// Give the copies the sprite's mesh, texture and sorting if any changed, and put the copies in place again. The
    /// game builds the sprite's mesh the first time the sprite is shown and then rebuilds that same mesh, so this
    /// mostly has nothing to do. The sorting goes along too, as the game can sort a sprite differently at times, so
    /// that the copies stay behind it and never cover it in flat colour.
    /// </summary>
    protected void Follow() {
        var mesh = _spriteFilter != null ? _spriteFilter.sharedMesh : null;
        if (mesh != _mesh) {
            _mesh = mesh;
            PlaceCopies();
        }

        if (SpriteRenderer != null) {
            var sorting = (SpriteRenderer.sortingLayerID, SpriteRenderer.sortingOrder);
            if (sorting != _sorting) {
                _sorting = sorting;
                for (var i = 0; i < _copies.Count; i++) {
                    CopyRenderers[i].sortingLayerID = sorting.sortingLayerID;
                    CopyRenderers[i].sortingOrder = sorting.sortingOrder + _copies[i].Order;
                }
            }
        }

        var spriteMaterial = SpriteRenderer != null ? SpriteRenderer.sharedMaterial : null;
        if (spriteMaterial != _spriteMaterial) {
            _spriteMaterial = spriteMaterial;

            var texture = spriteMaterial != null ? spriteMaterial.mainTexture : null;
            foreach (var material in _materials) {
                material.mainTexture = texture;
            }

            if (spriteMaterial != null) {
                OnSpriteMaterialChanged(spriteMaterial);
            }
        }
    }

    /// <summary>
    /// Put every copy where it goes, with the sprite's mesh.
    /// </summary>
    protected void PlaceCopies() {
        foreach (var (filter, direction, reach, _) in _copies) {
            filter.sharedMesh = _mesh;
            filter.transform.localPosition = CopyPosition(direction, reach, _mesh);
        }
    }

    protected virtual void LateUpdate() {
        Follow();
    }
}
