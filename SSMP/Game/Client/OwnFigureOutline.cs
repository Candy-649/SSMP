using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// An outline around this player's own figure: their hero in the room (see <see cref="FightOutline"/> for when it is
/// lit), or the figure that sits on the settings page to show what it looks like (Ui.SittingPreview).
///
/// One ring in the player's own name colour, drawn from copies of the figure (<see cref="SpriteOutline"/>), just
/// behind it among everything else in the room: the camera of the room draws by depth alone, so the copies go a little
/// further back than the figure, and less far back than the figure of another player, so that the outline shows over
/// that figure where the two overlap. Lit, it fades in quickly; put out, it fades away.
/// </summary>
internal class OwnFigureOutline : SpriteOutline {
    /// <summary>
    /// The name of the object that holds the outline.
    /// </summary>
    public const string ObjectName = "SSMP Own Figure Outline";

    /// <summary>
    /// How far behind the figure the copies are drawn. The game holds its hero at a depth of 0.004 and the figure of
    /// another player stands at 0.0041 (PlayerManager.PlayerObjectDepth), so the copies go between the two.
    /// </summary>
    private const float Depth = 0.00003f;

    /// <summary>
    /// How long the outline takes to show in full once lit, in seconds.
    /// </summary>
    private const float FadeInTime = 0.15f;

    /// <summary>
    /// How long the outline takes to fade away once put out, in seconds.
    /// </summary>
    private const float FadeOutTime = 0.5f;

    /// <summary>
    /// The material of the ring, this outline's own, since its colour fades.
    /// </summary>
    private Material? _material;

    /// <summary>
    /// The colour of the ring at its full strength.
    /// </summary>
    private Color _colour;

    /// <summary>
    /// Whether the outline is lit.
    /// </summary>
    private bool _lit;

    /// <summary>
    /// How strong the outline is drawn, from nothing to its full strength.
    /// </summary>
    private float _strength;

    /// <summary>
    /// Put an outline around the given figure, put out until it is lit.
    /// </summary>
    /// <param name="figure">The figure: an object with a sprite.</param>
    /// <param name="colour">The colour of the ring.</param>
    /// <param name="reach">How far the ring reaches out from the figure, in units of the room.</param>
    /// <returns>The outline, or null if the figure has nothing to draw or the flat shader is not there.</returns>
    public static OwnFigureOutline? Attach(GameObject figure, Color colour, float reach) {
        if (!figure.TryGetComponent<MeshFilter>(out _) || !figure.TryGetComponent<Renderer>(out _)) {
            return null;
        }

        var shader = FindFlatShader("own figure");
        if (shader == null) {
            return null;
        }

        RemoveLeftOver(figure, ObjectName);

        var holder = new GameObject(ObjectName) { layer = figure.layer };
        holder.transform.SetParent(figure.transform, false);

        var outline = holder.AddComponent<OwnFigureOutline>();
        outline.TakeSprite(figure);
        outline._material = new Material(shader);
        outline._colour = colour;
        outline.AddRing(outline._material, 0, reach);
        outline.Follow();
        outline.Draw();

        return outline;
    }

    /// <summary>
    /// Set the colour of the ring.
    /// </summary>
    /// <param name="colour">The colour.</param>
    public void SetColour(Color colour) {
        if (_colour == colour) {
            return;
        }

        _colour = colour;
        Draw();
    }

    /// <summary>
    /// Light the outline or put it out.
    /// </summary>
    /// <param name="lit">Whether it is lit.</param>
    public void SetLit(bool lit) {
        _lit = lit;
    }

    /// <inheritdoc />
    protected override Vector3 CopyPosition(Vector2 direction, float reach, Mesh? mesh) {
        // The reach is in units of the room, and the figure may be scaled: a hero turned the other way has a negative
        // scale, which the directions all around do not care about
        var scale = transform.lossyScale;
        return new Vector3(
            direction.x * reach / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
            direction.y * reach / Mathf.Max(Mathf.Abs(scale.y), 0.0001f),
            Depth / Mathf.Max(Mathf.Abs(scale.z), 0.0001f)
        );
    }

    /// <inheritdoc />
    protected override void OnSpriteMaterialChanged(Material spriteMaterial) {
        // Drawn in the same queue as the figure, so that the depth alone puts the copies behind it
        if (_material != null) {
            _material.renderQueue = spriteMaterial.renderQueue;
        }
    }

    /// <inheritdoc />
    protected override void LateUpdate() {
        base.LateUpdate();

        var target = _lit ? 1f : 0f;
        if (!Mathf.Approximately(_strength, target)) {
            var time = _lit ? FadeInTime : FadeOutTime;
            _strength = Mathf.MoveTowards(_strength, target, Time.deltaTime / time);
        }

        Draw();
    }

    /// <summary>
    /// Draw the copies at the strength and colour of the outline, and only while the figure itself is drawn: the game
    /// hides its hero in many places, and the outline goes with it.
    /// </summary>
    private void Draw() {
        if (_material != null) {
            _material.color = new Color(_colour.r, _colour.g, _colour.b, _colour.a * _strength);
        }

        var shown = _strength > 0f && SpriteRenderer != null && SpriteRenderer.enabled;
        foreach (var copyRenderer in CopyRenderers) {
            if (copyRenderer.enabled != shown) {
                copyRenderer.enabled = shown;
            }
        }
    }

    private void OnDestroy() {
        if (_material != null) {
            Destroy(_material);
        }
    }
}
