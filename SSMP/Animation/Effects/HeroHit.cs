using System;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// The streak of white light that the game draws across the hero as they are hit, shown on another player's character
/// at a third of its size (USER 10-25: "对，不要周围那种射线，只要那个受击的一横条，然后缩到1/3"). The game makes it
/// anew from its pool where the hero is hit, so other players never saw it. Only the streak shows: the lines that fly
/// out from the hero over the whole screen don't, nor does what the game draws over the dark of the room, its sound, the
/// shaking of the screen or the stop of the game, which are for the player who was hit (USER 9-23: "无论如何受击效果都不
/// 应该同步吧啊喂！"). It is drawn as a part of the character, so the dark of the room hides it where it hides them.
/// A hit is said at once and again for a moment, as animation updates can be lost.
/// </summary>
internal class HeroHit : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroHit Instance = new();

    /// <summary>
    /// Which streak the game drew, as it goes over the network: new kinds go at the end.
    /// </summary>
    private enum Kind : byte {
        // A hit that took one mask
        Single,

        // A hit that took more, whose streak is bigger
        Double
    }

    /// <summary>
    /// How big the streak on another player's character is, against the game's own.
    /// </summary>
    private const float Shrink = 1f / 3f;

    /// <summary>
    /// For how long a hit is said again after it, and how often, in seconds.
    /// </summary>
    private const float SaidFor = 0.25f;

    private const float SaidEvery = 0.1f;

    /// <summary>
    /// How long a streak shown on another player's character stays at most, in seconds: as long as the game keeps its
    /// own, which ends itself sooner.
    /// </summary>
    private const float Lasts = 1f;

    /// <summary>
    /// Sends the effect info of a hit to the other players, or null before the local hero is watched.
    /// </summary>
    private static Action<byte[]>? _send;

    /// <summary>
    /// The local hero whose hits are told.
    /// </summary>
    private static HeroController? _hero;

    /// <summary>
    /// The health of the local hero, with their blue health, as it was last seen: what a hit took is what is gone from
    /// it, which is what the game picks its streak by.
    /// </summary>
    private static int _health;

    /// <summary>
    /// The last hit of the local hero: counted round, so that each is shown once, its kind, and until when and when
    /// next it is said, in unscaled time.
    /// </summary>
    private static byte _hitId;
    private static Kind _kind;
    private static float _saidUntil;
    private static float _sayAt;

    /// <summary>
    /// Starts telling the other players of the local hero's hits, which may be a new hero or the same one again. This
    /// runs while the hero may still be being made, like <see cref="HeroChildEffects.Watch"/>.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a hit to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        _send = send;
        if (_hero != null) {
            _hero.OnTakenDamage -= OnTakenDamage;
        }

        _hero = hero;
        hero.OnTakenDamage -= OnTakenDamage;
        hero.OnTakenDamage += OnTakenDamage;
        _health = ReadHealth();
        _saidUntil = 0f;

        if (!hero.TryGetComponent<HeroHitWatcher>(out _)) {
            hero.gameObject.AddComponent<HeroHitWatcher>();
        }
    }

    /// <summary>
    /// The health of the local hero, with their blue health.
    /// </summary>
    private static int ReadHealth() {
        var playerData = PlayerData.instance;
        return playerData != null ? playerData.health + playerData.healthBlue : 0;
    }

    /// <summary>
    /// The local hero was hit. The game calls this from the middle of its hit, just before it draws the streak: one
    /// mask or none taken draws the small one, more the big one. What throws here must not stop the hit.
    /// </summary>
    private static void OnTakenDamage() {
        try {
            var health = ReadHealth();
            var taken = _health - health;
            _health = health;

            _hitId++;
            _kind = taken > 1 ? Kind.Double : Kind.Single;
            var now = Time.unscaledTime;
            _saidUntil = now + SaidFor;
            _sayAt = now + SaidEvery;
            _send?.Invoke([_hitId, (byte) _kind]);
        } catch (Exception e) {
            Logger.Warn($"Could not tell other players that the hero was hit: {e.Message}");
        }
    }

    /// <summary>
    /// Notes the health of the local hero and says the last hit again for a moment after it, once a frame.
    /// </summary>
    internal static void WatchLocal() {
        _health = ReadHealth();

        var now = Time.unscaledTime;
        if (now >= _saidUntil || now < _sayAt) {
            return;
        }

        _sayAt = now + SaidEvery;
        try {
            _send?.Invoke([_hitId, (byte) _kind]);
        } catch (Exception e) {
            Logger.Warn($"Could not tell other players again that the hero was hit: {e.Message}");
        }
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not { Length: >= 2 } || HeroController.SilentInstance is not { } hero) {
            return;
        }

        if (!playerObject.TryGetComponent<HeroHitHeard>(out var heard)) {
            heard = playerObject.AddComponent<HeroHitHeard>();
        }

        // Each hit once, and none older than one shown: a word of a hit said again can come after the next hit
        var hitId = effectInfo[0];
        if (heard.Heard && (sbyte) (hitId - heard.HitId) <= 0) {
            return;
        }

        heard.Heard = true;
        heard.HitId = hitId;
        var prefab = (Kind) effectInfo[1] == Kind.Double
            ? hero.takeHitDoubleEffectPrefab
            : hero.takeHitSingleEffectPrefab;
        try {
            Show(prefab, playerObject);
        } catch (Exception e) {
            Logger.Warn($"Could not show another player being hit: {e}");
        }
    }

    /// <summary>
    /// Draws the game's streak of a hit on another player's character, a third of its size, without the lines that fly
    /// out over the screen.
    /// </summary>
    private static void Show(GameObject? prefab, GameObject playerObject) {
        var place = playerObject.transform.position;
        var streak = HeroDust.MakeDust(prefab, place);
        if (streak == null) {
            return;
        }

        // What would draw it over the dark of the room sets that as it first switches on, so it goes before
        foreach (var overDark in streak.GetComponentsInChildren<OverMaskerBase>(true)) {
            Object.DestroyImmediate(overDark);
        }

        // The lines that fly out over the whole screen
        foreach (var particles in streak.GetComponentsInChildren<ParticleSystem>(true)) {
            if (particles.gameObject != streak) {
                particles.gameObject.SetActive(false);
            }
        }

        // Over the character, and under the dark of the room where the character is out of the light, as a part of them
        var body = playerObject.GetComponent<Renderer>();
        if (body != null) {
            foreach (var renderer in streak.GetComponentsInChildren<SpriteRenderer>(true)) {
                renderer.sortingLayerID = body.sortingLayerID;
                renderer.sortingOrder = body.sortingOrder + 1;
            }
        }

        // Smaller by a holder, as its animation may set its own size
        var holder = new GameObject("Partner Hit Streak");
        holder.transform.position = place;
        holder.transform.localScale = new Vector3(Shrink, Shrink, 1f);
        streak.transform.SetParent(holder.transform, false);
        streak.transform.localPosition = Vector3.zero;

        // It plays at once even off the screen, rather than when it comes into sight
        foreach (var animator in streak.GetComponentsInChildren<Animator>(true)) {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        streak.SetActive(true);
        EffectUtils.SafelyRemoveAutoRecycle(streak);
        Object.Destroy(holder, Lasts);
    }

    /// <summary>
    /// Forgets the hits heard of another player, for a character that leaves the room: it may come back, or be used
    /// for another player.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void Reset(GameObject playerObject) {
        if (playerObject.TryGetComponent<HeroHitHeard>(out var heard)) {
            heard.Heard = false;
        }
    }
}

/// <summary>
/// Watches the local hero once a frame, for <see cref="HeroHit"/>.
/// </summary>
internal class HeroHitWatcher : MonoBehaviour {
    private void LateUpdate() {
        HeroHit.WatchLocal();
    }
}

/// <summary>
/// The last hit heard of another player, on their character.
/// </summary>
internal class HeroHitHeard : MonoBehaviour {
    /// <summary>
    /// Whether a hit was shown, and the last one that was.
    /// </summary>
    [NonSerialized]
    public bool Heard;

    [NonSerialized]
    public byte HitId;
}
