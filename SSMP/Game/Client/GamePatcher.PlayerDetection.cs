using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// Contains multiplayer-aware player detection patches for enemy AI and PlayMaker actions.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// AlertRange line-of-sight mode value that uses the alert range object's own transform as the sight origin.
    /// </summary>
    private const int AlertRangeLineOfSightSelf = 1;

    /// <summary>
    /// AlertRange line-of-sight mode value that uses the alert range object's parent or initial parent as the sight origin.
    /// </summary>
    private const int AlertRangeLineOfSightParent = 2;

    /// <summary>
    /// Unity layer mask used for terrain obstruction checks during enemy line-of-sight tests.
    /// </summary>
    private const int TerrainLayerMask = 256;

    /// <summary>
    /// Reflected private field storing the AlertRange line-of-sight mode.
    /// </summary>
    private static readonly FieldInfo? AlertRangeLineOfSightField =
        typeof(AlertRange).GetField("lineOfSight", InstanceNonPublicFlags);

    /// <summary>
    /// Reflected private field storing the original parent transform used by AlertRange line-of-sight checks.
    /// </summary>
    private static readonly FieldInfo? AlertRangeInitialParentField =
        typeof(AlertRange).GetField("initialParent", InstanceNonPublicFlags);

    /// <summary>
    /// Reflected private field storing the alert ranges tracked by a LineOfSightDetector.
    /// </summary>
    private static readonly FieldInfo? LineOfSightDetectorAlertRangesField =
        typeof(LineOfSightDetector).GetField("alertRanges", InstanceNonPublicFlags);

    /// <summary>
    /// Reflected private field storing whether a LineOfSightDetector can currently see the hero.
    /// </summary>
    private static readonly FieldInfo? LineOfSightDetectorCanSeeHeroField =
        typeof(LineOfSightDetector).GetField("canSeeHero", InstanceNonPublicFlags);

    /// <summary>
    /// Hook for overriding the PlayMaker GetHero action with the enemy-approved multiplayer target.
    /// </summary>
    private Hook? _getHeroOnEnterHook;

    /// <summary>
    /// Hook for replacing AlertRange hero checks with multiplayer-aware approved-target checks.
    /// </summary>
    private Hook? _alertRangeIsHeroInRangeHook;

    /// <summary>
    /// Hook for replacing LineOfSightDetector visibility updates with approved-target visibility checks.
    /// </summary>
    private Hook? _lineOfSightDetectorUpdateHook;

    /// <summary>
    /// Alert range name fragments that identify ranges which may validate an existing approved target,
    /// but must not acquire or switch to a new target.
    /// </summary>
    private static readonly string[] NonAcquiringAlertRangeNameParts = [
        "Chomp",
        "Unalert",
        "Hit"
    ];

    /// <summary>
    /// Cache mapping AlertRange instances to their non-acquiring status to avoid native string name allocations.
    /// </summary>
    private static readonly ConditionalWeakTable<AlertRange, BoxedBool> NonAcquiringAlertRanges = [];

    /// <summary>
    /// What was worked out about one alert range, and the parent it hung under when that was worked out.
    /// </summary>
    private sealed class EnemyAlertRangeNote {
        /// <summary>
        /// The parent the range hung under at the time.
        /// </summary>
        public Transform? Parent;

        /// <summary>
        /// Whether something that could be fought was above it then.
        /// </summary>
        public bool BelongsToAnEnemy;

        /// <summary>
        /// When the creature last asked it whether a player was in it (see IsNoticedBy).
        /// </summary>
        public float AskedAt = float.NegativeInfinity;

        /// <summary>
        /// Whether it is about the player of this game alone (see IsAboutTheLocalPlayer), or null while that is not
        /// worked out yet.
        /// </summary>
        public bool? AboutTheLocalPlayer;

        /// <summary>
        /// When that is next worked out, while a state machine there has not started yet.
        /// </summary>
        public float LocalPlayerRetryAt = float.NegativeInfinity;

        /// <summary>
        /// Whether it is in a thing that each player has to themselves (see PersonalPlaces.IsThingPart), or null while
        /// that is not worked out yet.
        /// </summary>
        public bool? InPersonalThing;
    }

    /// <summary>
    /// Which alert ranges hang off something that can be fought, kept so the walk up the parents happens once per
    /// parent rather than every frame.
    /// </summary>
    private static readonly ConditionalWeakTable<AlertRange, EnemyAlertRangeNote> EnemyAlertRanges = [];

    /// <summary>
    /// Cache mapping AlertRange instances to their CachedCollider to avoid frame-by-frame GetComponent allocations.
    /// Uses ConditionalWeakTable to prevent memory leaks from keeping alive AlertRange instances that should be GC'ed.
    /// </summary>
    private static readonly ConditionalWeakTable<AlertRange, CachedCollider> AlertColliderCache = [];

    /// <summary>
    /// Cache mapping player GameObjects to their CachedPlayerInfo to avoid frame-by-frame native calls.
    /// Uses ConditionalWeakTable to prevent memory leaks from keeping alive player GameObjects.
    /// </summary>
    private static readonly ConditionalWeakTable<GameObject, CachedPlayerInfo> PlayerInfoCache = [];

    /// <summary>
    /// Registers detection-specific hooks.
    /// </summary>
    private void RegisterDetectionHooks() {
        _getHeroOnEnterHook = new Hook(
            typeof(GetHero).GetMethod("OnEnter", InstancePublicFlags),
            OnGetHeroOnEnter
        );

        _alertRangeIsHeroInRangeHook = new Hook(
            typeof(AlertRange).GetMethod("IsHeroInRange", InstancePublicFlags | InstanceNonPublicFlags)!,
            OnAlertRangeIsHeroInRange
        );

        _lineOfSightDetectorUpdateHook = new Hook(
            typeof(LineOfSightDetector).GetMethod("Update", InstancePublicFlags | InstanceNonPublicFlags)!,
            OnLineOfSightDetectorUpdate
        );
    }

    /// <summary>
    /// Disposes detection-specific hooks.
    /// </summary>
    private void DisposeDetectionHooks() {
        _getHeroOnEnterHook?.Dispose();
        _getHeroOnEnterHook = null;

        _alertRangeIsHeroInRangeHook?.Dispose();
        _alertRangeIsHeroInRangeHook = null;

        _lineOfSightDetectorUpdateHook?.Dispose();
        _lineOfSightDetectorUpdateHook = null;
    }

    /// <summary>
    /// Overrides the PlayMaker <see cref="GetHero"/> action to resolve the enemy-approved tracked player when possible.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The action instance being executed.</param>
    private static void OnGetHeroOnEnter(Action<GetHero> orig, GetHero self) {
        if (self.storeResult == null) {
            orig(self);
            return;
        }

        var requester = self.Fsm?.GameObject;
        if (requester != null && requester.GetComponentInParent<HealthManager>() != null) {
            // Whom it goes after, by the one rule for every creature, as it asks where the player is (see LookAsItAims)
            LookAsItAims(self.Fsm?.FsmComponent);
            var approvedTarget = GetFsmActionTarget(requester);
            if (approvedTarget == null) {
                orig(self);
                return;
            }

            self.storeResult.Value = approvedTarget;
            self.Finish();
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Replaces the hero-only alert-range check with tracked-player evaluation scoped to this alert range.
    /// </summary>
    /// <param name="orig">The game's own check, which sees the player of this game alone.</param>
    /// <param name="self">The alert range being queried.</param>
    /// <returns>True when the relevant tracked player is inside this alert range and visible; otherwise false.</returns>
    private static bool OnAlertRangeIsHeroInRange(Func<AlertRange, bool> orig, AlertRange self) {
        // A range about the player of this game alone answers as in a game with one player, also while a hit or touch
        // of the partner is replayed: what its state machine does next, it does to this game's player
        if (IsAboutTheLocalPlayer(self)) {
            return orig(self);
        }

        // A hit or touch of the partner that is being replayed goes by what the range said in their game when they
        // struck
        if (Save.CoopHits.TryGetReplayedInRange(self, out var replayed)) {
            return replayed;
        }

        var answer = AnswerWhetherAPlayerIsInAlertRange(self);

        // Said out loud when the answer is no while somebody is plainly standing there and can be seen, because
        // that is the whole of this going wrong and it leaves nothing else behind: whatever asked simply carries on
        // as though the room were empty. One line every few seconds is enough to catch it, and working out whether
        // to write one costs as much as answering the question, so it is only worked out that often.
        // A range at an end of a passage of dropping boxes says nobody is there, while a player waits in it, whenever
        // the order goes by the player crossing (see IsBoxPassageEnd)
        if (!answer && self != null && Time.unscaledTime >= _nextBlindRangeLogTime && !IsBoxPassageEnd(self)) {
            var inside = GetNearestPlayerInsideAlertRange(self);
            if (inside != null && HasLineOfSightToAlertRangeTarget(self, inside)) {
                _nextBlindRangeLogTime = Time.unscaledTime + BlindRangeLogInterval;

                var owner = GetEnemyTargetOwner(self.gameObject);
                var approved = owner == null ? null : GetApprovedEnemyTarget(owner);
                SSMP.Logging.Logger.Info(
                    $"'{self.name}' under '{(self.transform.parent == null ? "nothing" : self.transform.parent.name)}' " +
                    $"said nobody was there while {inside.name} stood in it and could be seen: belongs to an enemy " +
                    $"{BelongsToAnEnemy(self)}, may pick a target {CanAcquireMultiplayerTarget(self)}, owner " +
                    $"'{(owner == null ? "none" : owner.name)}', already going after " +
                    $"'{(approved == null ? "nobody" : approved.name)}'"
                );
            }
        }

        return answer;
    }

    /// <summary>
    /// How long to leave between lines about a range that says nobody is there while somebody is.
    /// </summary>
    private const float BlindRangeLogInterval = 3f;

    /// <summary>
    /// When the next line about a range that says nobody is there may be written.
    /// </summary>
    private static float _nextBlindRangeLogTime;

    /// <summary>
    /// Works out whether the player an alert range is about is inside it.
    /// </summary>
    /// <param name="self">The alert range being queried.</param>
    /// <returns>True when the relevant tracked player is inside this alert range and visible; otherwise false.</returns>
    private static bool AnswerWhetherAPlayerIsInAlertRange(AlertRange self) {
        // A range with nothing that can be fought above it is not asking which of two players something should
        // chase. It is a piece of the world - a barrel holding a trapped creature, a gate, a platform - asking
        // whether anybody is standing there, and either player is an answer to that. Sent down the path below it
        // was told "nobody" instead, every time and on both machines, because nothing ever approves a target for
        // something that is not an enemy. That is silent: the swing lands, the state machine asks whether anyone
        // is near enough to be worth opening for, hears no, and slides back to where it started leaving no mark.
        // A range at an end of a passage of dropping boxes goes by the player crossing it, while somebody is
        if (!BelongsToAnEnemy(self)) {
            return TryAnswerBoxPassageEnd(self, out var byCrossing) ? byCrossing : IsAnyPlayerInsideAlertRange(self);
        }

        if (EnemyAlertRanges.TryGetValue(self, out var note)) {
            note.AskedAt = Time.unscaledTime;
        }

        var owner = GetEnemyTargetOwner(self.gameObject);
        if (owner == null) {
            return false;
        }

        var approvedTarget = GetApprovedEnemyTarget(owner);
        var turns = TurnsOf(owner);

        if (!CanAcquireMultiplayerTarget(self)) {
            var inside = IsValidTargetForAlertRange(self, approvedTarget);
            NoteRangeAnswer(turns, inside);
            return inside;
        }

        // The one rule for every creature, whatever asks (see KeptTarget): while it keeps going after a player, each of
        // its ranges asks about them alone, with the other player standing nearer or not, and it turns from them by its
        // roll, once it no longer keeps them, or once its hold is over and the range finds the other player inside
        // while the one it keeps stands outside it (see MayTurnToOneInside)
        if (KeptTarget(owner) is { } kept) {
            var keptInside = IsValidTargetForAlertRange(self, kept);
            if (!keptInside && MayTurnToOneInside(owner)) {
                var inside = GetNearestPlayerInsideAlertRange(self, kept);
                if (inside != null && HasLineOfSightToAlertRangeTarget(self, inside)) {
                    TurnToOneInside(owner, kept, inside, self.name);
                    NoteRangeAnswer(TurnsOf(owner), true);
                    return true;
                }
            }

            NoteRangeAnswer(TurnsOf(owner), keptInside);
            return keptInside;
        }

        var approvedTargetIsValid = IsValidTargetForAlertRange(self, approvedTarget);
        NoteRangeAnswer(TurnsOf(owner), approvedTargetIsValid);

        // Nobody it keeps going after: the nearest player this range sees
        var candidateTarget = GetNearestPlayerInsideAlertRange(self);
        if (candidateTarget != null && HasLineOfSightToAlertRangeTarget(self, candidateTarget)) {
            ApproveEnemyTarget(owner, candidateTarget);
            return true;
        }

        return approvedTargetIsValid;
    }

    /// <summary>
    /// Whether the local player stands inside an alert range and can be seen from it, as the range would answer for this
    /// game's player alone, and the range may set its creature on a player. For the copy of a creature that the other
    /// game runs, which sees this game's player only by the figure it is told of (Entity.CheckWokenByLocalPlayer).
    /// </summary>
    /// <param name="alertRange">The alert range.</param>
    internal static bool SeesLocalPlayer(AlertRange alertRange) {
        var hero = HeroController.instance;
        if (hero == null || alertRange == null || !alertRange.isActiveAndEnabled) {
            return false;
        }

        // Nobody while the local player is between two rooms or down (PlayerTargetRegistry)
        var heroObject = hero.gameObject;
        return PlayerTargetRegistry.GetTrackedPlayers().Contains(heroObject) &&
               CanAcquireMultiplayerTarget(alertRange) &&
               IsPlayerInsideAlertRange(alertRange, heroObject) &&
               HasLineOfSightToAlertRangeTarget(alertRange, heroObject);
    }

    /// <summary>
    /// Whether an alert range hangs off something that can be fought, rather than off a piece of the world.
    ///
    /// Written down the first time, because this is asked of every alert range every frame and walking up the
    /// parents to find out costs more than the answer is worth. Nothing that is scenery today becomes an enemy.
    /// </summary>
    /// <param name="alertRange">The alert range to place.</param>
    /// <returns>
    /// <see langword="true"/> when something with health is above it; otherwise <see langword="false"/>.
    /// </returns>
    private static bool BelongsToAnEnemy(AlertRange alertRange) {
        if (alertRange == null) {
            return false;
        }

        // Worked out again whenever the range has been moved, because a good many enemies hand their ranges to
        // something else as they start up - a range that follows the enemy around is no use for guarding a spot -
        // and an answer from before the move would otherwise stand for the rest of the room.
        var parent = alertRange.transform.parent;
        if (EnemyAlertRanges.TryGetValue(alertRange, out var note) && note.Parent == parent) {
            return note.BelongsToAnEnemy;
        }

        // Counting the ones that are switched off too, so that an enemy which happens to be away at the moment the
        // question is first asked is not written down as scenery for the rest of the room.
        var belongs = alertRange.GetComponentInParent<HealthManager>(true) != null;

        if (note == null) {
            EnemyAlertRanges.Add(
                alertRange, new EnemyAlertRangeNote { Parent = parent, BelongsToAnEnemy = belongs }
            );
        } else {
            note.Parent = parent;
            note.BelongsToAnEnemy = belongs;
            note.AboutTheLocalPlayer = null;
        }

        return belongs;
    }

    /// <summary>
    /// Whether either player is standing inside an alert range and can be seen from it.
    /// </summary>
    /// <param name="alertRange">The alert range to test.</param>
    /// <returns>
    /// <see langword="true"/> when one of the players is there; otherwise <see langword="false"/>.
    /// </returns>
    private static bool IsAnyPlayerInsideAlertRange(AlertRange alertRange) {
        if (alertRange == null) {
            return false;
        }

        var player = GetNearestPlayerInsideAlertRange(alertRange);

        return player != null && HasLineOfSightToAlertRangeTarget(alertRange, player);
    }

    /// <summary>
    /// Determines whether a previously approved target is still valid for a specific alert range.
    /// </summary>
    /// <param name="alertRange">The alert range to validate against.</param>
    /// <param name="target">The approved target to validate.</param>
    /// <returns>
    /// <see langword="true"/> when the target is inside the alert range and visible; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private static bool IsValidTargetForAlertRange(AlertRange alertRange, GameObject? target) {
        return target != null &&
               IsPlayerInsideAlertRange(alertRange, target) &&
               HasLineOfSightToAlertRangeTarget(alertRange, target);
    }

    /// <summary>
    /// Recomputes line-of-sight against the enemy-approved tracked player instead of the local hero singleton.
    /// </summary>
    /// <param name="self">The detector being updated.</param>
    private static void OnLineOfSightDetectorUpdate(LineOfSightDetector self) {
        var target = GetNearestVisiblePlayer(self);
        var canSeeHero = false;

        if (target != null) {
            var origin = (Vector2) self.transform.position;
            var targetPosition = (Vector2) GetTargetAimPosition(target);
            var direction = targetPosition - origin;

            canSeeHero = direction.sqrMagnitude > 0f &&
                         !Helper.Raycast2D(origin, direction.normalized, direction.magnitude, TerrainLayerMask);

            Debug.DrawLine(origin, targetPosition, canSeeHero ? Color.green : Color.yellow);
        }

        LineOfSightDetectorCanSeeHeroField?.SetValue(self, canSeeHero);
    }

    /// <summary>
    /// Resolves the transform used as the line-of-sight origin for an alert range.
    /// </summary>
    /// <param name="alertRange">The alert range to inspect.</param>
    /// <returns>The transform to use for line-of-sight checks, or null if none applies.</returns>
    private static Transform? GetAlertRangeLineOfSightOrigin(AlertRange alertRange) {
        var lineOfSight = (int?) AlertRangeLineOfSightField?.GetValue(alertRange) ?? 0;
        return lineOfSight switch {
            AlertRangeLineOfSightSelf => alertRange.transform,
            AlertRangeLineOfSightParent => alertRange.transform.parent
                ? alertRange.transform.parent
                : AlertRangeInitialParentField?.GetValue(alertRange) as Transform,
            _ => null
        };
    }

    /// <summary>
    /// Finds or acquires the visible tracked player for a line-of-sight detector.
    /// </summary>
    /// <param name="detector">The detector requesting a target.</param>
    /// <returns>The approved or newly acquired target if visible; otherwise null.</returns>
    private static GameObject? GetNearestVisiblePlayer(LineOfSightDetector detector) {
        var approvedTarget = GetApprovedEnemyTarget(detector.gameObject);

        if (LineOfSightDetectorAlertRangesField?.GetValue(detector) is not AlertRange[] alertRanges ||
            alertRanges.Length == 0) {
            return approvedTarget;
        }

        // What its sight finds is what its ranges would (see IsFighting), and the ranges it looks through are ones it
        // notices players with (see NoteSightAsked)
        var owner = GetEnemyTargetOwner(detector.gameObject);
        var turns = TurnsOf(owner);
        NoteRangeAnswer(turns, false);
        NoteSightAsked(alertRanges);

        // The one rule for every creature (see KeptTarget): its sight looks for the player it keeps going after alone, and
        // loses sight of them without turning to the other player. It turned at once before, and back again as soon as
        // the first came back into sight, several times a second while both stood at the edge of what it sees (USER
        // 10-23: "这个遭遇战的虫子真的有换目标吗？！")
        if (owner != null && KeptTarget(owner) is { } kept) {
            foreach (var alertRange in alertRanges) {
                if (alertRange != null && IsValidTargetForAlertRange(alertRange, kept)) {
                    NoteRangeAnswer(TurnsOf(owner), true);
                    return kept;
                }
            }

            return null;
        }

        // Nobody it keeps going after: the nearest player its ranges see
        GameObject? candidateTarget = null;
        var candidateDistance = float.PositiveInfinity;

        foreach (var alertRange in alertRanges) {
            if (alertRange == null || !CanAcquireMultiplayerTarget(alertRange)) {
                continue;
            }

            var target = GetNearestPlayerInsideAlertRange(alertRange);
            if (target == null || !HasLineOfSightToAlertRangeTarget(alertRange, target)) {
                continue;
            }

            var distance = ((Vector2) detector.transform.position - (Vector2) target.transform.position)
                .sqrMagnitude;
            if (distance >= candidateDistance) {
                continue;
            }

            candidateDistance = distance;
            candidateTarget = target;
        }

        if (candidateTarget != null && owner != null) {
            ApproveEnemyTarget(owner, candidateTarget);
        }

        return candidateTarget;
    }

    /// <summary>
    /// Notes that a creature's sight asked the ranges it looks through, as a range is noted when the creature asks it
    /// itself: they are ranges it notices players with (see AwarenessRangesOf), and a player it goes after who is in
    /// none of them is not noticed (see IsNoticedBy). Asked by nothing else, they counted for nothing, and a flyer kept
    /// going after a player who had flown out of its sight while the other one stood in it.
    /// </summary>
    /// <param name="alertRanges">The ranges.</param>
    private static void NoteSightAsked(AlertRange[] alertRanges) {
        var now = Time.unscaledTime;
        foreach (var alertRange in alertRanges) {
            if (alertRange != null && BelongsToAnEnemy(alertRange) &&
                EnemyAlertRanges.TryGetValue(alertRange, out var note)) {
                note.AskedAt = now;
            }
        }
    }

    /// <summary>
    /// Finds the nearest tracked player that is physically inside the supplied alert range.
    /// </summary>
    /// <param name="alertRange">The alert range to test.</param>
    /// <param name="except">A player to leave out, if any.</param>
    /// <returns>The nearest valid player inside the alert range, or <see langword="null"/>.</returns>
    private static GameObject? GetNearestPlayerInsideAlertRange(AlertRange alertRange, GameObject? except = null) {
        GameObject? bestPlayer = null;
        var bestDistance = float.PositiveInfinity;

        var players = PlayerTargetRegistry.GetTrackedPlayers();
        foreach (var player in players) {
            if (player == null || player == except) {
                continue;
            }

            if (!IsPlayerInsideAlertRange(alertRange, player)) {
                continue;
            }

            var distance = ((Vector2) alertRange.transform.position - (Vector2) player.transform.position).sqrMagnitude;
            if (distance >= bestDistance) {
                continue;
            }

            bestDistance = distance;
            bestPlayer = player;
        }

        return bestPlayer;
    }


    /// <summary>
    /// Determines whether a tracked player should be considered inside the supplied alert range.
    /// </summary>
    /// <param name="alertRange">The alert range to test against.</param>
    /// <param name="player">The tracked player object to test.</param>
    /// <returns>
    /// <see langword="true"/> when the player is already tracked by the vanilla alert range or one of its colliders
    /// overlaps one of the switched-on colliders of the alert range; otherwise <see langword="false"/>.
    /// </returns>
    private static bool IsPlayerInsideAlertRange(AlertRange alertRange, GameObject player) {
        if (player == null || !player.activeInHierarchy) {
            return false;
        }

        if (alertRange.InsideGameObjects.Contains(player)) {
            return true;
        }

        // Every collider of the range, as the game hears its own player in any of them that is switched on. A range
        // can keep a shape that is switched off beside the one it reaches with: the attack range of the bombers keeps
        // a switched-off polygon ahead of its box, and the first collider alone was that polygon, so a bomber that
        // went after the partner never had them in reach and never threw (USER 10-24 "不扔炸弹")
        if (!AlertColliderCache.TryGetValue(alertRange, out var cached)) {
            cached = new CachedCollider(alertRange.GetComponents<Collider2D>());
            AlertColliderCache.Add(alertRange, cached);
        }

        var alertColliders = cached.Colliders;
        if (alertColliders.Length == 0) {
            return false;
        }

        var currentFrame = Time.frameCount;

        // Cache alert collider bounds per frame
        if (cached.FrameCount != currentFrame) {
            cached.FrameCount = currentFrame;
            for (var i = 0; i < alertColliders.Length; i++) {
                var alertCollider = alertColliders[i];
                cached.Bounds[i] = alertCollider != null && alertCollider.enabled ? alertCollider.bounds : default;
            }
        }

        // Retrieve or create cached player info
        if (!PlayerInfoCache.TryGetValue(player, out var playerInfo)) {
            playerInfo = new CachedPlayerInfo();
            PlayerInfoCache.Add(player, playerInfo);
        }

        // Cache player bounds and position per frame
        if (playerInfo.FrameCount != currentFrame) {
            playerInfo.FrameCount = currentFrame;
            playerInfo.Position = player.transform.position;

            var colliders = PlayerTargetRegistry.GetPlayerColliders(player);
            if (playerInfo.ColliderBounds.Length != colliders.Length) {
                playerInfo.ColliderBounds = new Bounds[colliders.Length];
            }

            for (int i = 0; i < colliders.Length; i++) {
                var col = colliders[i];
                if (col != null && col.enabled) {
                    playerInfo.ColliderBounds[i] = col.bounds;
                } else {
                    playerInfo.ColliderBounds[i] = default;
                }
            }
        }

        for (var i = 0; i < alertColliders.Length; i++) {
            var alertCollider = alertColliders[i];
            if (alertCollider == null || !alertCollider.enabled) {
                continue;
            }

            if (IsPlayerInsideAlertCollider(alertCollider, cached.Bounds[i], playerInfo)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a tracked player overlaps one switched-on collider of an alert range.
    /// </summary>
    /// <param name="alertCollider">The collider of the alert range.</param>
    /// <param name="alertBounds">Its bounds, as they were cached this frame.</param>
    /// <param name="playerInfo">The player's colliders and position, as they were cached this frame.</param>
    private static bool IsPlayerInsideAlertCollider(
        Collider2D alertCollider,
        Bounds alertBounds,
        CachedPlayerInfo playerInfo
    ) {
        // Check collider intersections using cached bounds in 2D
        foreach (var playerBounds in playerInfo.ColliderBounds) {
            if (playerBounds.extents is { x: 0f, y: 0f }) {
                continue;
            }

            // 2D box intersection check (ignores Z)
            if (alertBounds.min.x <= playerBounds.max.x && alertBounds.max.x >= playerBounds.min.x &&
                alertBounds.min.y <= playerBounds.max.y && alertBounds.max.y >= playerBounds.min.y) {
                return true;
            }
        }

        // Fallback check (only if player position is inside the alert bounds in 2D)
        var playerPos = playerInfo.Position;
        if (playerPos.x >= alertBounds.min.x && playerPos.x <= alertBounds.max.x &&
            playerPos.y >= alertBounds.min.y && playerPos.y <= alertBounds.max.y) {
            return alertCollider.OverlapPoint(playerPos);
        }

        return false;
    }

    /// <summary>
    /// Gets the best available target position for sight checks.
    /// </summary>
    /// <param name="target">The target object to inspect.</param>
    /// <returns>The center of the target collider when available; otherwise the target transform position.</returns>
    private static Vector3 GetTargetAimPosition(GameObject target) {
        var collider = target.GetComponentInChildren<Collider2D>();
        return collider != null ? collider.bounds.center : target.transform.position;
    }

    /// <summary>
    /// Determines whether an alert range is allowed to acquire and approve a new multiplayer target.
    /// </summary>
    /// <param name="alertRange">The alert range to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when the range belongs to an enemy detection setup and may acquire a target;
    /// otherwise <see langword="false"/>.
    /// </returns>
    private static bool CanAcquireMultiplayerTarget(AlertRange alertRange) {
        if (alertRange == null || !alertRange.isActiveAndEnabled) {
            return false;
        }

        if (alertRange.GetComponentInParent<HealthManager>() == null) {
            return false;
        }

        if (NonAcquiringAlertRanges.TryGetValue(alertRange, out var boxed)) {
            return !boxed.Value;
        }

        var owner = GetEnemyTargetOwner(alertRange.gameObject);
        EntityRegistryEntry? entry = null;
        if (owner != null) {
            EntityRegistry.TryGetEntry(owner, out entry);
        }

        var isNonAcquiring = false;
        if (entry is { NonAcquiringRanges: not null }) {
            foreach (var blockedNamePart in entry.NonAcquiringRanges) {
                if (alertRange.name.Contains(blockedNamePart, StringComparison.OrdinalIgnoreCase)) {
                    isNonAcquiring = true;
                    break;
                }
            }
        } else {
            isNonAcquiring = IsNonAcquiringAlertRangeName(alertRange.name);
        }

        NonAcquiringAlertRanges.Add(alertRange, new BoxedBool { Value = isNonAcquiring });

        return !isNonAcquiring;
    }

    /// <summary>
    /// Determines whether an alert range name belongs to a range that should only validate an existing target,
    /// not acquire a new one.
    /// </summary>
    /// <param name="rangeName">The alert range object name.</param>
    /// <returns>
    /// <see langword="true"/> when the range should not acquire a new multiplayer target; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private static bool IsNonAcquiringAlertRangeName(string rangeName) {
        foreach (var blockedNamePart in NonAcquiringAlertRangeNameParts) {
            if (rangeName.Contains(blockedNamePart, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the alert range has line of sight to a target when line-of-sight checks are enabled.
    /// </summary>
    /// <param name="alertRange">The alert range performing the check.</param>
    /// <param name="target">The target to check visibility against.</param>
    /// <returns>
    /// <see langword="true"/> when line of sight is not required or the target is visible; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private static bool HasLineOfSightToAlertRangeTarget(AlertRange alertRange, GameObject target) {
        if (!alertRange.ChecksLineOfSight) {
            return true;
        }

        var origin = GetAlertRangeLineOfSightOrigin(alertRange);
        if (origin == null) {
            return false;
        }

        var targetPosition = GetTargetAimPosition(target);
        return !Helper.LineCast2DHit(origin.position, targetPosition, TerrainLayerMask, out _);
    }
}

/// <summary>
/// A wrapper class for boolean values to allow storing them in a ConditionalWeakTable.
/// </summary>
internal class BoxedBool {
    /// <summary>
    /// The wrapped boolean value.
    /// </summary>
    public bool Value;
}

/// <summary>
/// A wrapper class for the Collider2D references of an alert range to allow caching them in a ConditionalWeakTable.
/// </summary>
internal class CachedCollider(Collider2D[] colliders) {
    /// <summary>
    /// Every collider on the alert range's object, switched on or not; empty if none is attached.
    /// </summary>
    public readonly Collider2D[] Colliders = colliders;

    /// <summary>
    /// The frame number when the bounds were last updated.
    /// </summary>
    public int FrameCount = -1;

    /// <summary>
    /// The cached bounds of each collider, by its place in <see cref="Colliders"/>; empty bounds for one that was
    /// switched off.
    /// </summary>
    public readonly Bounds[] Bounds = new Bounds[colliders.Length];
}

/// <summary>
/// A cache container for player bounds and position to avoid native property access calls within a frame.
/// </summary>
internal class CachedPlayerInfo {
    /// <summary>
    /// The frame number when the player info was last cached.
    /// </summary>
    public int FrameCount = -1;

    /// <summary>
    /// The cached bounds of the player's colliders.
    /// </summary>
    public Bounds[] ColliderBounds = [];

    /// <summary>
    /// The cached transform position of the player.
    /// </summary>
    public Vector3 Position;
}
