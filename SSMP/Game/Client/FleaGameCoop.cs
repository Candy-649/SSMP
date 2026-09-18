using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
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
/// The score of each player is theirs alone and their own game saves it, so nothing is taken over here. In the game
/// that does not control the fleas, a hit of the local player that really did tink a flea broadcasts the scoring
/// event that its own fleas can no longer send, and from there that game counts it, shows it and saves it exactly
/// as it does when playing alone. The game that controls the fleas needs none of that and is left untouched, and so
/// is playing alone.
///
/// Both games do keep their own running count of the fleas they hit and tell the other, so that each player can be
/// shown where the two of them stand. That count is sent as a total rather than as single points, so a lost or a
/// repeated message cannot make it drift.
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
    /// Event that a flea sends when it was hit. Every game listens for it: the one that counts and saves the score,
    /// and the one that decides what to send out next, which in one of the three games gets harder for every point.
    /// </summary>
    private const string ScoreEventName = "SCORE";

    /// <summary>
    /// Events that mean a game of the festival is starting over, so the counts of both players begin again.
    /// </summary>
    private static readonly string[] ResetEventNames = ["GAME BEGIN PLAY", "RESET FLEA GAMES"];

    /// <summary>
    /// Name of the state machine of a game that decides what to send out next. In one of the three games it makes
    /// the game harder for every point scored; the other two never answer to a point at all, so telling all of them
    /// is both simpler and safer than picking one out by name.
    /// </summary>
    private const string DirectorFsmName = "Game Specific Control";

    /// <summary>
    /// Name of the state machine that runs one of the games, counts the score and saves it.
    /// </summary>
    private const string MasterFsmName = "flea_game_master_control";

    /// <summary>
    /// The states of a game that mean nobody is playing it. Whether the local player is in a game is read from the
    /// game itself rather than from the events it sends, because one event name of a game is not told apart from
    /// another's in what can be read of them.
    /// </summary>
    private static readonly string[] IdleStateNames = ["Init", "Inactive"];

    /// <summary>
    /// Event that puts every flea away again at the end of a game.
    /// </summary>
    private const string ResetEventName = "RESET FLEA GAMES";

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
    /// Reflected field with the text a badge of the score board draws its number into. A badge added at runtime has
    /// none, so it is taken from the badge it was made from.
    /// </summary>
    private static readonly FieldInfo? BadgeScoreTextField =
        typeof(ScoreBoardUIBadgeBase).GetField("scoreText", InstanceFlags);

    /// <summary>
    /// The net client, for telling the partner how many fleas the local player has hit.
    /// </summary>
    private readonly NetClient _netClient;

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
    /// Hook that notices a game of the festival starting over.
    /// </summary>
    private Hook? _eventRegisterSendEventHook;

    /// <summary>
    /// Hook that adds the partner's row when a score board appears.
    /// </summary>
    private Hook? _scoreBoardOnEnableHook;

    /// <summary>
    /// How many fleas the local player has hit in the game being played.
    /// </summary>
    private ulong _localScore;

    /// <summary>
    /// How many fleas the partner has hit in the game being played.
    /// </summary>
    public int PartnerScore { get; private set; }

    /// <summary>
    /// How many of the partner's points the game that runs the fleas has already been told about. Only the game
    /// that runs them makes them harder, and both players see the fleas it sends out, so a point the partner scores
    /// has to reach it or the two of them would be playing the same fleas at different speeds.
    /// </summary>
    private int _partnerScoreFed;

    /// <summary>
    /// Whether the partner is still in a game of the festival.
    /// </summary>
    private bool _partnerPlaying;

    /// <summary>
    /// Whether putting the fleas away was held back because the partner was still playing, and so still has to
    /// happen once they are finished too.
    /// </summary>
    private bool _resetHeld;

    public FleaGameCoop(NetClient netClient, EntityManager entityManager, Func<ushort?> getPartnerId) {
        _netClient = netClient;
        _entityManager = entityManager;
        _getPartnerId = getPartnerId;
    }

    /// <summary>
    /// Registers the hooks of the flea games.
    /// </summary>
    public void RegisterHooks() {
        var hitMethod = typeof(TinkEffect).GetMethod("Hit", InstanceFlags);
        if (hitMethod == null) {
            Logger.Error("TinkEffect does not declare Hit; the flea games were not hooked");
        } else {
            _tinkEffectHitHook = new Hook(hitMethod, OnTinkEffectHit);
        }

        var sendMethod = typeof(EventRegister).GetMethod(
            "SendEvent",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [typeof(string), typeof(GameObject)],
            null
        );
        if (sendMethod == null) {
            Logger.Error("EventRegister does not declare SendEvent; the flea game counts will not start over");
        } else {
            _eventRegisterSendEventHook = new Hook(sendMethod, OnEventRegisterSendEvent);
        }

        var boardMethod = typeof(ScoreBoardUI).GetMethod("OnEnable", InstanceFlags);
        if (boardMethod == null) {
            Logger.Error("ScoreBoardUI does not declare OnEnable; the partner's row was not added");
        } else {
            _scoreBoardOnEnableHook = new Hook(boardMethod, OnScoreBoardEnable);
        }
    }

    /// <summary>
    /// Deregisters the hooks of the flea games.
    /// </summary>
    public void DeregisterHooks() {
        _tinkEffectHitHook?.Dispose();
        _tinkEffectHitHook = null;

        _eventRegisterSendEventHook?.Dispose();
        _eventRegisterSendEventHook = null;

        _scoreBoardOnEnableHook?.Dispose();
        _scoreBoardOnEnableHook = null;

        ForgetScores();
    }

    /// <summary>
    /// Takes how many fleas the partner has hit. The update carries their total rather than a single point, so one
    /// that arrives late or twice can only ever be ignored.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnPartnerScore(ClientPlayerData player, CoopSaveUpdate update) {
        if (_getPartnerId() != player.Id) {
            return;
        }

        PartnerScore = System.Math.Max(PartnerScore, (int) update.Key);
        _partnerPlaying = update.Part != 0;

        if (_entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost) {
            FeedPartnerPoints();
            ReleaseHeldReset();
        }
    }

    /// <summary>
    /// Puts the fleas away after all, once neither player is in a game any more. Held back while the partner was
    /// still playing, since they are watching these same fleas, and nothing else would ever do it afterwards.
    /// </summary>
    private void ReleaseHeldReset() {
        if (!_resetHeld || _partnerPlaying || IsLocalPlaying()) {
            return;
        }

        _resetHeld = false;

        try {
            EventRegister.SendEvent(ResetEventName, null);
        } catch (Exception e) {
            Logger.Warn($"Could not put the fleas away: {e.GetType()}, {e.Message}");
        }
    }

    /// <summary>
    /// Makes the game as hard for both players as the better of the two has earned. Only the game that runs the
    /// fleas sends them out, so only it decides the difficulty, and its own player's points already reach it. This
    /// hands it the partner's, without going near the score that each player keeps and saves for themselves.
    /// </summary>
    private void FeedPartnerPoints() {
        if (_partnerScoreFed >= PartnerScore) {
            return;
        }

        try {
            var directors = FindFsms(DirectorFsmName);
            if (directors.Length == 0) {
                return;
            }

            for (; _partnerScoreFed < PartnerScore; _partnerScoreFed++) {
                foreach (var director in directors) {
                    if (director != null) {
                        director.SendEvent(ScoreEventName);
                    }
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not pass on a point of the partner: {e.GetType()}, {e.Message}");
        }
    }

    /// <summary>
    /// Hook for <see cref="TinkEffect.Hit"/>. A hit of the local player that tinked a flea counts for them, and in
    /// a game that does not control the fleas it also scores, which the flea's own state machine cannot do while it
    /// is off.
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

        // Only a two-player save has a partner to be shown against
        if (_getPartnerId() == null) {
            return response;
        }

        // A copy of the partner's attack scores in their game, not in this one
        if (RemoteAttackComponent.IsRemoteAttack(hit.Source)) {
            return response;
        }

        try {
            if (!IsScoringFlea(self)) {
                return response;
            }

            // The game that controls the fleas already scores through their own state machines
            if (!_entityManager.IsSceneHost) {
                EventRegister.SendEvent(ScoreEventName, null);
            }

            _localScore++;
            SendLocalScore();
        } catch (Exception e) {
            Logger.Warn($"Could not score a hit on a flea: {e.GetType()}, {e.Message}");
        }

        return response;
    }

    /// <summary>
    /// Hook for <see cref="EventRegister.SendEvent(string, UnityEngine.GameObject)"/>, which notices a game of the
    /// festival starting over so that the counts of both players do not carry into it.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="eventName">The event being sent.</param>
    /// <param name="source">The object that sent it.</param>
    private void OnEventRegisterSendEvent(Action<string, GameObject> orig, string eventName, GameObject source) {
        // Putting the fleas away is what the game that runs them does when its own player is finished. The other
        // player watches those same fleas, so doing it while they are still playing would reparent, stop and hide
        // every flea in the middle of their game. It waits until they are finished too.
        if (eventName == ResetEventName && _partnerPlaying && _getPartnerId() != null &&
            _entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost) {
            _resetHeld = true;
            return;
        }

        orig(eventName, source);

        if (eventName != null && Array.IndexOf(ResetEventNames, eventName) >= 0) {
            ForgetScores();
        }
    }

    /// <summary>
    /// Begins both counts again, and tells the partner so that neither is left showing the last game.
    /// </summary>
    private void ForgetScores() {
        PartnerScore = 0;
        _partnerScoreFed = 0;
        _partnerPlaying = false;
        _resetHeld = false;
        _localScore = 0;

        // Always told, even from nothing to nothing, because this is also how the partner hears that the local
        // player is no longer in a game and that the fleas may be put away
        SendLocalScore();
    }

    /// <summary>
    /// Tells the partner how many fleas the local player has hit in the game being played.
    /// </summary>
    private void SendLocalScore() {
        if (_getPartnerId() is not { } partnerId || !_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.SetCoopSaveUpdate(
            new CoopSaveUpdate {
                TargetId = partnerId,
                Kind = CoopSaveUpdateKind.FleaGameScore,
                Key = _localScore,
                Part = (ushort) (IsLocalPlaying() ? 1 : 0)
            }
        );
    }

    /// <summary>
    /// Returns whether the local player is in a game of the festival.
    /// </summary>
    private static bool IsLocalPlaying() {
        foreach (var master in FindFsms(MasterFsmName)) {
            if (master != null && Array.IndexOf(IdleStateNames, master.ActiveStateName) < 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the state machines of a given name in the scene, including the ones on objects that are switched off.
    /// </summary>
    /// <param name="name">The name of the state machine.</param>
    /// <returns>The state machines found.</returns>
    private static PlayMakerFSM[] FindFsms(string name) {
        return Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                     .Where(fsm => fsm.Fsm != null && fsm.Fsm.Name == name)
                     .ToArray();
    }

    /// <summary>
    /// Hook for the score board appearing, which gives the partner a row of their own on it. The board sorts the
    /// rows it finds by their score, so the row places itself among the others rather than being put somewhere.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The score board.</param>
    private void OnScoreBoardEnable(Action<ScoreBoardUI> orig, ScoreBoardUI self) {
        orig(self);

        if (_getPartnerId() == null) {
            return;
        }

        try {
            if (AddPartnerRow(self)) {
                self.Refresh();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not add the partner's row to the score board: {e.GetType()}, {e.Message}");
        }
    }

    /// <summary>
    /// Gives the partner a row on a score board, made from the row that shows the local player's own score so that
    /// it matches the others. Does nothing if it already has one.
    /// </summary>
    /// <param name="board">The score board.</param>
    /// <returns>Whether a row was added.</returns>
    private bool AddPartnerRow(ScoreBoardUI board) {
        if (board.GetComponentInChildren<FleaPartnerScoreBadge>(true) != null) {
            return false;
        }

        var hero = board.GetComponentInChildren<ScoreBoardUIBadgeHero>(true);
        if (hero == null) {
            return false;
        }

        var row = Object.Instantiate(hero.gameObject, hero.transform.parent);
        row.name = "SSMP Partner Score";

        // The copy is still the local player's row until its own badge is taken off it
        foreach (var copied in row.GetComponents<ScoreBoardUIBadgeBase>()) {
            Object.DestroyImmediate(copied);
        }

        var badge = row.AddComponent<FleaPartnerScoreBadge>();

        // A badge draws its number into a text of its own object, which a badge added at runtime has no reference
        // to, so it is taken from the row this one was copied from
        BadgeScoreTextField?.SetValue(badge, BadgeScoreTextField.GetValue(hero));
        badge.GetScore = () => PartnerScore;

        row.SetActive(true);
        return true;
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
}

/// <summary>
/// A row of a score board of the festival that shows how many fleas the partner has hit. The board asks every row
/// it finds for its score and puts them in order, so this one ranks itself against the local player and the
/// characters already on the board without any of them being touched.
/// </summary>
internal class FleaPartnerScoreBadge : ScoreBoardUIBadgeBase {
    /// <summary>
    /// Gets how many fleas the partner has hit.
    /// </summary>
    public Func<int>? GetScore { get; set; }

    /// <inheritdoc />
    public override int Score => GetScore?.Invoke() ?? 0;

    /// <inheritdoc />
    public override bool IsVisible => GetScore != null;
}
