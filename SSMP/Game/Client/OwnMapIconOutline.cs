using UnityEngine;

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
/// One ring in the player's own name colour, drawn from copies of the head (<see cref="SpriteOutline"/>), below the
/// head. A dark ring used to lie around it; the player wanted none (USER 2026-10-20: "无论是大黄蜂的描边还是头像的描边
/// 都不要加黑色外圈"). The head's own soft glow is in the ring too, which makes it soft at its edge.
/// </summary>
internal class OwnMapIconOutline : SpriteOutline {
    /// <summary>
    /// The name of the object that holds the outline. The other players' heads are copied from this player's own
    /// head and drop the object by this name.
    /// </summary>
    public const string ObjectName = "SSMP Own Outline";

    /// <summary>
    /// How far below this player's own head, in sorting order, the other players' heads are drawn: below the ring of
    /// the outline, so that a head over this player's own does not cover the outline.
    /// </summary>
    public const int OtherHeadsBelow = 3;

    /// <summary>
    /// How far the ring reaches out from the head, as a part of the head's larger side. At 0.08 it was too thick for
    /// the player.
    /// </summary>
    private const float Reach = 0.05f;

    /// <summary>
    /// The material of the ring. Made once and kept, like the shader: there is only ever one outline, and a material
    /// of an outline whose head was never shown would outlive it, because Unity does not call OnDestroy on an object
    /// that was never active.
    /// </summary>
    private static Material? _material;

    /// <summary>
    /// Put an outline around the given head.
    /// </summary>
    /// <param name="head">This player's own head on the map.</param>
    /// <param name="colour">The colour of the ring.</param>
    /// <returns>The outline, or null if the head has nothing to draw or the flat shader is not there.</returns>
    public static OwnMapIconOutline? Attach(GameObject head, Color colour) {
        if (!head.TryGetComponent<MeshFilter>(out _) || !head.TryGetComponent<Renderer>(out _)) {
            return null;
        }

        var shader = FindFlatShader("own head on the map");
        if (shader == null) {
            return null;
        }

        RemoveLeftOver(head, ObjectName);

        if (_material == null) {
            _material = new Material(shader);
        }

        _material.color = colour;

        var holder = new GameObject(ObjectName) { layer = head.layer };
        holder.transform.SetParent(head.transform, false);

        var outline = holder.AddComponent<OwnMapIconOutline>();
        outline.TakeSprite(head);

        // The ring, then the head itself
        outline.AddRing(_material, -1, Reach);
        outline.Follow();

        return outline;
    }

    /// <summary>
    /// Set the colour of the ring.
    /// </summary>
    /// <param name="colour">The colour.</param>
    public void SetColour(Color colour) {
        if (_material != null) {
            _material.color = colour;
        }
    }

    /// <inheritdoc />
    protected override Vector3 CopyPosition(Vector2 direction, float reach, Mesh? mesh) {
        var size = mesh != null ? Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.y) : 0f;
        return direction * (reach * size);
    }
}
