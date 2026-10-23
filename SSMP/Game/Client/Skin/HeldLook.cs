using System;
using System.Collections.Generic;
using SSMP.Animation.Effects;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// Draws a creature that holds or carries another player in that player's skin and cloak colour. Such a creature draws
/// the player in its own sprites while the game hides the player, from sheets of sprites that have the hero in them,
/// and this game dresses those sheets in the local player's skin and colour: the other player showed in the local
/// player's look for as long as the creature had them. While another player is hidden, the sprites of such sheets near
/// them are drawn from that player's own copies of the sheets, which wear what that player wears. Each camera draws
/// them so and puts their own material back after drawing: their animation and the skin mods put theirs on them again
/// whenever their picture changes, and a creature near the player that does not show them looks the same either way,
/// as a skin of the hero changes only the hero.
/// </summary>
internal static class HeldLook {
    /// <summary>
    /// How often the creatures near a hidden player are looked at again, in seconds.
    /// </summary>
    internal const float LookAgainEvery = 0.2f;

    /// <summary>
    /// How often all the sprites of the room are looked through again for creatures that may draw a player, in seconds:
    /// often enough for a creature that came meanwhile, and the first look at a player just hidden always does.
    /// </summary>
    private const float LookThroughEvery = 2f;

    /// <summary>
    /// How far from what a sprite draws a hidden player may be for the sprite to draw them, in units.
    /// </summary>
    private const float Reach = 2f;

    /// <summary>
    /// The sheets of sprites in which creatures draw the hero that they hold or carry: those of the sheets that Player
    /// Color has masks of the cloak for that are drawn by creatures and traps rather than by the hero or by a room.
    /// </summary>
    private static readonly HashSet<string> CreatureSheets = new(StringComparer.Ordinal) {
        "Coral Tendril Cln", "Dustroach Cln", "Dustroach Pollywog Cln", "Farmer Scissors Cln", "First Weaver Cln",
        "Gloom Beast Cln", "Hornet Heart Grab Cln", "Hornet Strung Up Cln", "Lost Lace Cln", "Mite Trap Cln",
        "Phantom Cln", "Rosary Thief Cln", "Silk Boss Cln", "Silk Boss DeathEmbrace Cln", "Slab Fly Large Cage Cln",
        "Slab Fly Vomit Cln", "Spinner Cln", "Swamp Barnacle Cln", "Wood Witch Cln", "Wraith Cln"
    };

    /// <summary>
    /// Gives the copy of a sheet of sprites that the character of another player draws from, made if there is none
    /// yet, or null (see <see cref="PlayerManager.FigureSheet"/>).
    /// </summary>
    internal static Func<GameObject, tk2dSpriteCollectionData, tk2dSpriteCollectionData?>? SheetOf;

    /// <summary>
    /// The sprites of the room that may draw a player, found by the last look through all of them.
    /// </summary>
    private static readonly List<tk2dBaseSprite> Candidates = [];

    /// <summary>
    /// When all the sprites of the room are looked through again, in unscaled time.
    /// </summary>
    private static float _lookThroughAt;

    /// <summary>
    /// The sprites drawn in the look of each hidden player, by the character of that player.
    /// </summary>
    private static readonly Dictionary<GameObject, List<Drawn>> Drawing = new();

    /// <summary>
    /// The renderers that the camera drawing now draws with another material, each with its own.
    /// </summary>
    private static readonly List<(Renderer Renderer, Material Own)> Swapped = [];

    /// <summary>
    /// Whether the cameras are watched.
    /// </summary>
    private static bool _watching;

    /// <summary>
    /// Looks at the creatures near a hidden player, and draws those that may draw the player in the player's look.
    /// </summary>
    /// <param name="figure">The character of the player.</param>
    /// <param name="lookThrough">Whether to look through all the sprites of the room first.</param>
    public static void Look(GameObject figure, bool lookThrough) {
        if (SheetOf == null) {
            return;
        }

        if (lookThrough || Time.unscaledTime >= _lookThroughAt) {
            LookThrough();
        }

        if (!Drawing.TryGetValue(figure, out var drawn)) {
            Drawing[figure] = drawn = [];
        }

        drawn.Clear();

        var place = (Vector2) figure.transform.position;
        var hero = HeroController.UnsafeInstance;
        var heroHidden = hero != null && HeroHidden.LocalHeroHidden;
        var heroPlace = hero != null ? (Vector2) hero.transform.position : default;
        foreach (var sprite in Candidates) {
            if (sprite == null) {
                continue;
            }

            var sheet = sprite.Collection;
            if (sheet == null || !CreatureSheets.Contains(sheet.name) ||
                !sprite.TryGetComponent<Renderer>(out var renderer) || !renderer.enabled) {
                continue;
            }

            // The local player draws themselves while a creature hides them too
            var bounds = renderer.bounds;
            var distance = Distance(bounds, place);
            if (distance > Reach || heroHidden && Distance(bounds, heroPlace) < distance) {
                continue;
            }

            if (SheetOf(figure, sheet) is { } copy) {
                drawn.Add(new Drawn(sprite, renderer, sheet, copy));
            }
        }

        if (drawn.Count == 0) {
            Drawing.Remove(figure);
        }

        Watch();
    }

    /// <summary>
    /// Stops drawing anything in the look of a player who shows again.
    /// </summary>
    /// <param name="figure">The character of the player.</param>
    public static void Stop(GameObject figure) {
        if (Drawing.Remove(figure)) {
            Watch();
        }
    }

    /// <summary>
    /// Looks through all the sprites of the room for those of creatures that may draw a player, leaving out the local
    /// hero's and those of the characters of the other players.
    /// </summary>
    private static void LookThrough() {
        _lookThroughAt = Time.unscaledTime + LookThroughEvery;
        Candidates.Clear();

        var hero = HeroController.UnsafeInstance;
        var heroPlace = hero != null ? hero.transform : null;
        foreach (var sprite in Object.FindObjectsByType<tk2dBaseSprite>(FindObjectsSortMode.None)) {
            var sheet = sprite.Collection;
            if (sheet == null || !CreatureSheets.Contains(sheet.name)) {
                continue;
            }

            var place = sprite.transform;
            if (heroPlace != null && place.IsChildOf(heroPlace) ||
                place.root.name.StartsWith(PlayerManager.PlayerContainerName, StringComparison.Ordinal)) {
                continue;
            }

            Candidates.Add(sprite);
        }
    }

    /// <summary>
    /// How far a point is from what a renderer draws, across the screen.
    /// </summary>
    private static float Distance(Bounds bounds, Vector2 point) {
        var min = bounds.min;
        var max = bounds.max;
        var x = Mathf.Max(min.x - point.x, 0f, point.x - max.x);
        var y = Mathf.Max(min.y - point.y, 0f, point.y - max.y);
        return Mathf.Sqrt(x * x + y * y);
    }

    /// <summary>
    /// Watches the cameras while anything is drawn in another player's look, and stops when nothing is.
    /// </summary>
    private static void Watch() {
        var watch = Drawing.Count > 0;
        if (watch == _watching) {
            return;
        }

        _watching = watch;
        if (watch) {
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        } else {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            PutBack();
        }
    }

    /// <summary>
    /// Has a camera about to draw draw the sprites near hidden players with the materials of the players' own copies of
    /// their sheets: each sprite's current picture as the copy would draw it.
    /// </summary>
    private static void OnPreCull(Camera camera) {
        // What a camera that never finished drawing left behind
        PutBack();

        foreach (var drawn in Drawing.Values) {
            foreach (var (sprite, renderer, original, copy) in drawn) {
                if (sprite == null || renderer == null || copy == null || !renderer.enabled ||
                    sprite.Collection != original) {
                    continue;
                }

                var definitions = copy.spriteDefinitions;
                var id = sprite.spriteId;
                if (definitions == null || id < 0 || id >= definitions.Length ||
                    definitions[id] is not { } definition) {
                    continue;
                }

                var material = definition.materialInst != null ? definition.materialInst : definition.material;
                if (material == null) {
                    continue;
                }

                Swapped.Add((renderer, renderer.sharedMaterial));
                renderer.sharedMaterial = material;
            }
        }
    }

    /// <summary>
    /// Puts their own materials back once a camera has drawn.
    /// </summary>
    private static void OnPostRender(Camera camera) {
        PutBack();
    }

    /// <summary>
    /// Puts back the materials of the sprites given others, the last first.
    /// </summary>
    private static void PutBack() {
        for (var i = Swapped.Count - 1; i >= 0; i--) {
            var (renderer, own) = Swapped[i];
            if (renderer != null) {
                renderer.sharedMaterial = own;
            }
        }

        Swapped.Clear();
    }

    /// <summary>
    /// A sprite drawn in another player's look: the sprite, what draws it, the sheet it draws from and the player's
    /// copy of that sheet.
    /// </summary>
    private readonly record struct Drawn(
        tk2dBaseSprite Sprite,
        Renderer Renderer,
        tk2dSpriteCollectionData Original,
        tk2dSpriteCollectionData Copy
    );
}
