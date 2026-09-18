using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op rules for the games of the festival, where hitting the flying fleas scores points.
///
/// The fleas are entities, so the scene host runs their state machines and the other game only receives where they
/// are. That keeps both players looking at the same fleas, but it also means the copies in the game that does not
/// control them never react to being hit: their state machines are switched off, so the hit that would send the
/// scoring event lands on nothing. The game that controls them has the opposite problem in the same place, since a
/// copy of the partner's attack is not allowed to tink at all, so it can never score for them either.
///
/// The score of each player is theirs alone and their own game saves it, so nothing is shared here: this only fills
/// the hole. In the game that does not control the fleas, a hit of the local player that really did tink a flea
/// sends the scoring event to that game's own director, which then counts it, shows it and saves it exactly as it
/// does when playing alone. The game that controls the fleas needs none of this and is left untouched, and so is
/// playing alone.
///
/// Known limit: the game that does not control the fleas cannot see what state a flea is in, because the copy's
/// state machine is off. It therefore counts a tink that the controlling game might not have counted, such as one
/// on a flea that was already falling. Feedback stays immediate for the player who swung, which is the rule this
/// mod follows everywhere else.
/// </summary>
internal class FleaGameCoop {
    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Name of the state machine that runs one of the games, counts the score and saves it.
    /// </summary>
    private const string MasterFsmName = "flea_game_master_control";

    /// <summary>
    /// State of the director in which a game is being played and accepts scoring events.
    /// </summary>
    private const string PlayingStateName = "Playing";

    /// <summary>
    /// Event that a flea sends to the director of its game when it was hit.
    /// </summary>
    private const string ScoreEventName = "SCORE";

    /// <summary>
    /// The entity types of the fleas that can be hit for points. The fleas that fly in before a game starts are left
    /// out, since their state machine has no scoring state at all.
    /// </summary>
    private static readonly EntityType[] ScoringFleaTypes = [
        EntityType.BellfleaBouncer,
        EntityType.BellfleaBouncerGiant,
        EntityType.BellfleaJuggler,
        EntityType.BellfleaJugglerGiant,
        EntityType.BellfleaSwooper
    ];

    /// <summary>
    /// Cache telling whether a tink belongs to a flea that scores. The hook runs for every tinkable object in the
    /// game, so the walk up the hierarchy is done once per object instead of once per hit.
    /// </summary>
    private static readonly ConditionalWeakTable<TinkEffect, BoxedBool> ScoringFleaTinks = [];

    /// <summary>
    /// The entity manager, which tells whether this game controls the entities of the scene.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// Gets the ID of the partner of the two-player save, or null when no save is paired.
    /// </summary>
    private readonly Func<ushort?> _getPartnerId;

    /// <summary>
    /// Hook that notices a hit of the local player landing on a flea.
    /// </summary>
    private Hook? _tinkEffectHitHook;

    /// <summary>
    /// The directors of the games in the current scene, or null while they have not been looked up yet.
    /// </summary>
    private PlayMakerFSM[]? _masterFsms;

    public FleaGameCoop(EntityManager entityManager, Func<ushort?> getPartnerId) {
        _entityManager = entityManager;
        _getPartnerId = getPartnerId;
    }

    /// <summary>
    /// Registers the hooks of the flea games.
    /// </summary>
    public void RegisterHooks() {
        var method = typeof(TinkEffect).GetMethod("Hit", InstanceFlags);
        if (method == null) {
            Logger.Error("TinkEffect does not declare Hit; the flea games were not hooked");
        } else {
            _tinkEffectHitHook = new Hook(method, OnTinkEffectHit);
        }

        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>
    /// Deregisters the hooks of the flea games.
    /// </summary>
    public void DeregisterHooks() {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;

        _tinkEffectHitHook?.Dispose();
        _tinkEffectHitHook = null;

        _masterFsms = null;
    }

    /// <summary>
    /// Forgets the directors of the previous scene.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        _masterFsms = null;
    }

    /// <summary>
    /// Hook for <see cref="TinkEffect.Hit"/>. In a game that does not control the fleas, a hit of the local player
    /// that tinked one of them scores in this game, which the flea's own state machine cannot do while it is off.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The tink that was hit.</param>
    /// <param name="hit">The hit.</param>
    /// <returns>What the tink answered to the hit.</returns>
    private IHitResponder.HitResponse OnTinkEffectHit(
        Func<TinkEffect, HitInstance, IHitResponder.HitResponse> orig,
        TinkEffect self,
        HitInstance hit
    ) {
        var response = orig(self, hit);

        // Nothing tinked, so there is nothing to score
        if (response.response == IHitResponder.Response.None) {
            return response;
        }

        // Only a two-player save has a partner whose game could be running the fleas
        if (_getPartnerId() == null) {
            return response;
        }

        // The game that controls the fleas already scores through their own state machines
        if (!_entityManager.IsSceneRoleDetermined || _entityManager.IsSceneHost) {
            return response;
        }

        // A copy of the partner's attack scores in their game, not in this one
        if (RemoteAttackComponent.IsRemoteAttack(hit.Source)) {
            return response;
        }

        try {
            if (IsScoringFlea(self)) {
                SendScore();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not score a hit on a flea: {e.GetType()}, {e.Message}");
        }

        return response;
    }

    /// <summary>
    /// Returns whether a tink belongs to a flea of a game that can be hit for points. The tinks sit below the flea,
    /// so its entry is looked for on the objects above them.
    /// </summary>
    /// <param name="tink">The tink that was hit.</param>
    /// <returns>Whether hitting it scores a point.</returns>
    private static bool IsScoringFlea(TinkEffect tink) {
        if (ScoringFleaTinks.TryGetValue(tink, out var known)) {
            return known.Value;
        }

        var isScoringFlea = false;
        for (var transform = tink.transform; transform != null; transform = transform.parent) {
            if (!EntityRegistry.TryGetEntry(transform.gameObject, out var entry)) {
                continue;
            }

            isScoringFlea = ScoringFleaTypes.Contains(entry.Type);
            break;
        }

        ScoringFleaTinks.Add(tink, new BoxedBool { Value = isScoringFlea });
        return isScoringFlea;
    }

    /// <summary>
    /// Scores a point in the game that is being played, which counts it, shows it and saves it as it does alone.
    /// </summary>
    private void SendScore() {
        _masterFsms ??= Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                              .Where(fsm => fsm.Fsm is { Name: MasterFsmName })
                              .ToArray();

        foreach (var fsm in _masterFsms) {
            if (fsm == null || fsm.ActiveStateName != PlayingStateName) {
                continue;
            }

            fsm.SendEvent(ScoreEventName);
            return;
        }
    }
}
