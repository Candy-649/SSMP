using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// An outline around this player's own head on the map.
///
/// Every head on the map is the same icon: the other players' heads are copies of this player's own. So the head
/// that stands out is this player's own, the same rule as the name over their head, and only on this machine.
/// Marking the others instead would mark everyone but one once there are more than two, and the eye goes to what
/// stands out. The game shows this player's own head only while they wear the compass, so without it every head on
/// the map is someone else's.
///
/// The outline is drawn from copies of the head itself. Each copy draws the head's own mesh in one flat colour, moved
/// a little to one side, below the head. A bright ring in the player's own name colour lies inside a dark one, so
/// that it shows on the pale and the dark parts of the map alike. The copies share the head's mesh, which the game
/// rebuilds in place when the head's animation changes frame, so the outline keeps to the head's shape.
/// </summary>
internal class OwnMapIconOutline : MonoBehaviour {
    /// <summary>
    /// The name of the object that holds the outline. The other players' heads are copied from this player's own
    /// head and drop the object by this name.
    /// </summary>
    public const string ObjectName = "SSMP Own Outline";

    /// <summary>
    /// How far below this player's own head, in sorting order, the other players' heads are drawn: below both rings
    /// of the outline, so that a head over this player's own does not cover the outline.
    /// </summary>
    public const int OtherHeadsBelow = 3;

    /// <summary>
    /// The shader that draws a mesh in one flat colour with only its texture's alpha: Unity's own text shader, which
    /// the game ships in its default resources.
    /// </summary>
    private const string FlatShaderName = "GUI/Text Shader";

    /// <summary>
    /// How many directions the copies of each ring are moved in.
    /// </summary>
    private const int Directions = 12;

    /// <summary>
    /// How far the bright ring reaches out from the head, as a part of the head's larger side.
    /// </summary>
    private const float BrightReach = 0.08f;

    /// <summary>
    /// How far the dark ring reaches out from the head, as a part of the head's larger side.
    /// </summary>
    private const float DarkReach = 0.13f;

    /// <summary>
    /// Where the inner copies of a ring sit, as a part of its reach. Copies only at the full reach would leave gaps
    /// beside thin parts of the head, such as its horns.
    /// </summary>
    private const float InnerReach = 0.6f;

    /// <summary>
    /// The colour of the dark ring.
    /// </summary>
    private static readonly Color DarkColour = new(0.08f, 0.06f, 0.05f, 1f);

    /// <summary>
    /// The flat shader, once found.
    /// </summary>
    private static Shader? _flatShader;

    /// <summary>
    /// Whether the flat shader was looked for and is not there, so that it is looked for and reported once.
    /// </summary>
    private static bool _flatShaderMissing;

    /// <summary>
    /// The copies of the head, each with the direction it is moved in and how far, as a part of the head's larger
    /// side.
    /// </summary>
    private readonly List<(MeshFilter filter, Vector2 direction, float reach)> _copies = [];

    /// <summary>
    /// The mesh filter of the head, which holds the mesh the copies share.
    /// </summary>
    private MeshFilter? _headFilter;

    /// <summary>
    /// The renderer of the head, whose texture the copies draw with.
    /// </summary>
    private Renderer? _headRenderer;

    /// <summary>
    /// The material of the bright ring.
    /// </summary>
    private Material? _brightMaterial;

    /// <summary>
    /// The material of the dark ring.
    /// </summary>
    private Material? _darkMaterial;

    /// <summary>
    /// The mesh the copies were last given.
    /// </summary>
    private Mesh? _mesh;

    /// <summary>
    /// The texture the copies were last given.
    /// </summary>
    private Texture? _texture;

    /// <summary>
    /// Put an outline around the given head.
    /// </summary>
    /// <param name="head">This player's own head on the map.</param>
    /// <param name="colour">The colour of the bright ring.</param>
    /// <returns>The outline, or null if the head has nothing to draw or the flat shader is not there.</returns>
    public static OwnMapIconOutline? Attach(GameObject head, Color colour) {
        if (!head.TryGetComponent<MeshFilter>(out var headFilter) ||
            !head.TryGetComponent<Renderer>(out var headRenderer)) {
            return null;
        }

        var shader = FindFlatShader();
        if (shader == null) {
            return null;
        }

        var holder = new GameObject(ObjectName) { layer = head.layer };
        holder.transform.SetParent(head.transform, false);

        var outline = holder.AddComponent<OwnMapIconOutline>();
        outline._headFilter = headFilter;
        outline._headRenderer = headRenderer;
        outline._brightMaterial = new Material(shader) { color = colour };
        outline._darkMaterial = new Material(shader) { color = DarkColour };

        // The dark ring lowest, then the bright ring, then the head itself
        outline.AddRing(outline._darkMaterial, headRenderer.sortingOrder - 2, DarkReach);
        outline.AddRing(outline._brightMaterial, headRenderer.sortingOrder - 1, BrightReach);
        outline.Follow();

        return outline;
    }

    /// <summary>
    /// Set the colour of the bright ring.
    /// </summary>
    /// <param name="colour">The colour.</param>
    public void SetColour(Color colour) {
        if (_brightMaterial != null) {
            _brightMaterial.color = colour;
        }
    }

    /// <summary>
    /// Add the copies of one ring.
    /// </summary>
    /// <param name="material">The material the ring is drawn with.</param>
    /// <param name="sortingOrder">The sorting order the ring is drawn at.</param>
    /// <param name="reach">How far the ring reaches out from the head, as a part of the head's larger side.</param>
    private void AddRing(Material material, int sortingOrder, float reach) {
        foreach (var part in new[] { InnerReach, 1f }) {
            for (var i = 0; i < Directions; i++) {
                var angle = i * 2f * Mathf.PI / Directions;

                var copy = new GameObject("Outline Copy") { layer = gameObject.layer };
                copy.transform.SetParent(transform, false);

                var filter = copy.AddComponent<MeshFilter>();
                var renderer = copy.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.sortingLayerID = _headRenderer!.sortingLayerID;
                renderer.sortingOrder = sortingOrder;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                _copies.Add((filter, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), reach * part));
            }
        }
    }

    /// <summary>
    /// Give the copies the head's mesh and texture if either changed. The game builds the head's mesh the first time
    /// the head is shown and then rebuilds that same mesh, so this mostly has nothing to do.
    /// </summary>
    private void Follow() {
        var mesh = _headFilter != null ? _headFilter.sharedMesh : null;
        if (mesh != _mesh) {
            _mesh = mesh;

            var size = mesh != null ? Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.y) : 0f;
            foreach (var (filter, direction, reach) in _copies) {
                filter.sharedMesh = mesh;
                filter.transform.localPosition = direction * (reach * size);
            }
        }

        var headMaterial = _headRenderer != null ? _headRenderer.sharedMaterial : null;
        var texture = headMaterial != null ? headMaterial.mainTexture : null;
        if (texture != _texture) {
            _texture = texture;

            if (_brightMaterial != null) {
                _brightMaterial.mainTexture = texture;
            }

            if (_darkMaterial != null) {
                _darkMaterial.mainTexture = texture;
            }
        }
    }

    private void LateUpdate() {
        Follow();
    }

    private void OnDestroy() {
        if (_brightMaterial != null) {
            Destroy(_brightMaterial);
        }

        if (_darkMaterial != null) {
            Destroy(_darkMaterial);
        }
    }

    /// <summary>
    /// Find the flat shader, or report once that it is not there.
    /// </summary>
    /// <returns>The shader, or null if it is not there.</returns>
    private static Shader? FindFlatShader() {
        if (_flatShader == null && !_flatShaderMissing) {
            _flatShader = Shader.Find(FlatShaderName);

            if (_flatShader == null) {
                _flatShaderMissing = true;
                Logger.Warn($"Shader \"{FlatShaderName}\" not found, own head on the map gets no outline");
            }
        }

        return _flatShader;
    }
}
