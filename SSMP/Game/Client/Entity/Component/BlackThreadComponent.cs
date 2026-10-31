using System.Collections;
using System.Collections.Generic;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries what the third act does to a creature (BlackThreadState): whether it is one that turns,
/// whether it has turned, which of its attacks it strikes with, and each time it strikes. The game rolls all of it as
/// the creature starts and every time it looks for the player, and each game rolled its own, against its own save and
/// its own player: one player saw a creature turn and strike that the other saw do nothing, and was struck by an attack
/// that the other never saw (USER 10-31, "那个怪变强我觉得可以直接修，就按照以前我们怎么做这类掷骰子的方法修"). As with the
/// rest of a creature, the game that runs it rolls, and the other games take what came out: their copy never rolls,
/// turns when the creature turns, and strikes with the same attack, the same way round, when it strikes; what it strikes
/// is the player of that game, as with every other attack of a copy. How it looks for whom to strike, whom it turns
/// for, and the blows of the other players that turn it go by the same rules for every player
/// (GamePatcher.BlackThread).
///
/// What the game that runs it rolled is also written into the save of the others, where the game keeps it until the
/// player rests, so that a game that runs the room later - after the other player left, or on coming back - starts the
/// creature the same way, and a game that takes the creature over goes on with it as it was. A creature that dies is
/// written down as not turning, as the game does.
internal class BlackThreadComponent : EntityComponent {
    /// <summary>
    /// What the game writes after the name of the creature's object to keep in the save whether it turns.
    /// </summary>
    private const string SavedIdSuffix = "_BlackThread";

    /// <summary>
    /// The attack index that stands for none.
    /// </summary>
    private const byte NoAttack = byte.MaxValue;

    /// <summary>
    /// The component of each creature's own object and copy, by its state, for the hooks on the game's own code.
    /// </summary>
    private static readonly Dictionary<BlackThreadState, BlackThreadComponent> ByState = new();

    /// <summary>
    /// The states that this component set up the way the game that runs the creature said (see <see cref="SetUp"/>),
    /// which take what it says from then on.
    /// </summary>
    private static readonly HashSet<BlackThreadState> SetUpHere = [];

    /// <summary>
    /// Whether the component is making a followed state do what the game that runs the creature did, which lets the
    /// state turn and strike (GamePatcher.BlackThread lets a copy do neither by itself).
    /// </summary>
    public static bool IsApplying { get; private set; }

    /// <summary>
    /// The state of the room's own object and of the copy.
    /// </summary>
    private readonly HostClientPair<BlackThreadState> _states;

    /// <summary>
    /// Whether it was settled which game runs the room, before which the room's own object of the game that expects to
    /// run it rolls as the game does.
    /// </summary>
    private bool _isDetermined;

    /// <summary>
    /// Whether the game that runs the creature said yet what it is.
    /// </summary>
    private bool _isKnown;

    /// <summary>
    /// What the game that runs the creature said: whether it turns, whether it turned, and which attack it strikes with.
    /// </summary>
    private bool _isThreaded, _isVisible;

    /// <inheritdoc cref="_isThreaded"/>
    private byte _attack = NoAttack;

    /// <summary>
    /// The number of the last strike that the game that runs the creature said, which a copy strikes once for each new
    /// one.
    /// </summary>
    private ushort _strike;

    /// <summary>
    /// The number of the last time that the creature of the game that runs it began to darken to strike, which a copy
    /// darkens once for each new one: the game darkens a creature over a while before it strikes, from the moment it
    /// sings.
    /// </summary>
    private ushort _windUp;

    /// <summary>
    /// What was last sent, so that what changes is sent once: whether it turns, whether it turned, and the attack.
    /// </summary>
    private bool _sentThreaded, _sentVisible;

    /// <inheritdoc cref="_sentThreaded"/>
    private byte _sentAttack = NoAttack;

    /// <summary>
    /// Whether anything was sent yet since this game began to run the creature.
    /// </summary>
    private bool _hasSent;

    /// <summary>
    /// Whether another game ran the creature when it was last settled who does, so that this game running it from now
    /// on is a takeover.
    /// </summary>
    private bool _ranElsewhere;

    /// <summary>
    /// How the attack of the last strike sat on the creature, which goes along with what the creature is.
    /// </summary>
    private Vector2 _strikeScale = Vector2.one;

    /// <inheritdoc cref="_strikeScale"/>
    private float _strikeAngle;

    /// <summary>
    /// The copy's replay of the last strike, which a new strike ends.
    /// </summary>
    private Coroutine? _strikeRoutine;

    /// <summary>
    /// Whether the attack of the strike that the copy replays is still to be put the way it sat on the creature of the
    /// game that runs it (see <see cref="ApplyToldStrike"/>), and how.
    /// </summary>
    private bool _hasToldStrike;

    /// <inheritdoc cref="_hasToldStrike"/>
    private Vector2 _toldStrikeScale;

    /// <inheritdoc cref="_hasToldStrike"/>
    private float _toldStrikeAngle;

    public BlackThreadComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<BlackThreadState> states
    ) : base(netClient, entityId, gameObject) {
        _states = states;
        ByState[states.Host] = this;
        ByState[states.Client] = this;
    }

    /// <summary>
    /// The component of a state, and whether the state is the copy's, if the state is of an entity.
    /// </summary>
    public static bool TryFind(BlackThreadState? state, out BlackThreadComponent component, out bool isCopy) {
        if (state != null && ByState.TryGetValue(state, out component!)) {
            isCopy = component._states.Client == state;
            return true;
        }

        component = null!;
        isCopy = false;
        return false;
    }

    /// <summary>
    /// Whether this game runs the creature of a state: it is the room's own object, and it was settled that this game
    /// runs the room. Until that is settled, a room's own object that this game left running goes by the game's own
    /// rules, for this game's player alone.
    /// </summary>
    public static bool RunsHere(BlackThreadState? state) {
        return TryFind(state, out var component, out var isCopy) && !isCopy && !component.IsControlled;
    }

    /// <summary>
    /// Sets a state up as the game does as it starts the creature, for the hook on SetupThreaded. The copy, and the room's
    /// own object while another game runs the creature, set up without rolling, by what that game said; so does the
    /// room's own object of a game that took the creature over, as it first starts, once it was told what the creature
    /// is. Otherwise the game rolls, as it does when the creature's own FSM starts it over later.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="isFirst">Whether it is the first time, as the creature starts.</param>
    /// <param name="isCopy">Whether the state is the copy's.</param>
    /// <returns>Whether it was set up here, so that the game's own setup is to be left out.</returns>
    public bool SetUp(BlackThreadState state, bool isFirst, bool isCopy) {
        var follows = isCopy || (_isDetermined && IsControlled);
        var tookOver = isFirst && !isCopy && !IsControlled && _isKnown;
        if (!follows && !tookOver) {
            return false;
        }

        if (!state.isBlackThreadWorld || state.isThreaded || state.singFsm == null) {
            return true;
        }

        var threaded = _isKnown && _isThreaded;
        state.isThreaded = threaded;

        // Only a creature that this game runs comes back turned as it is switched on, as the game has it (GoOn): what
        // this game's own object set itself up with before would have turned it as this game took it over, whatever
        // the game that ran it said
        state.startAlreadyBlackThreadedOnEnable = false;

        if (isFirst && state.healthManager != null) {
            state.healthManager.OnDeath += () => WriteSaved(false, "it died");
        }

        state.PreSpawnBlackThreadEffects();
        if (isFirst) {
            state.FirstThreadedSetUp();
        }

        state.SetBlackThreadAmount(0f);
        SetUpHere.Add(state);
        Logger.Info(
            $"'{state.name}' set up its third-act state as {(tookOver ? "taken over" : "followed")}: " +
            $"{(_isKnown ? threaded ? _isVisible ? "turned" : "turns" : "doesn't turn" : "not told yet")}"
        );

        if (!threaded) {
            return true;
        }

        // Its health is what the game that ran it said, which already has the turn in it
        TakeAttack(state);
        if (tookOver) {
            GoOn(state);
        } else if (_isVisible) {
            TurnAtOnce(state);
        }

        return true;
    }

    /// <summary>
    /// Has the room's own object of a creature that turns go on as this game takes the creature over, the way the game
    /// has such a creature go on: turned, as the game that ran it said, with its attacks; or about to turn, at once if it
    /// comes turned, never by itself if something else turns it, and otherwise as soon as a player stays near. One that
    /// comes turned comes so again as it is switched on, as the game has it.
    /// </summary>
    private void GoOn(BlackThreadState state) {
        if (!state.isThreaded || !state.gameObject.activeInHierarchy) {
            return;
        }

        TakeAttack(state);
        if (!state.hasChosenAttack) {
            state.ChooseAttack(false);
        }

        var comesTurned = state.startThreaded || BlackThreadCore.IsAnyActive;
        state.startAlreadyBlackThreadedOnEnable = comesTurned;

        // Turned already, here or as the game that ran it said: its attacks ended as it was switched off
        if (state.IsVisiblyThreaded) {
            Logger.Info($"'{state.name}' goes on turned as this game takes it over");
            state.StartAttackTest();
            return;
        }

        if (_isKnown && _isVisible) {
            TurnAtOnce(state);
            return;
        }

        if (comesTurned) {
            Logger.Info($"'{state.name}' comes turned as this game takes it over");
            state.StartCoroutine(state.WaitForActive());
            state.SetBlackThreadAmount(1f);
            state.StartAttackTest();
            return;
        }

        if (state.willBeThreaded || state.waitRangeRoutine != null) {
            return;
        }

        Logger.Info($"'{state.name}' waits for a player to stay near before it turns, as this game takes it over");
        state.waitRangeRoutine = state.StartCoroutine(state.WaitForInRange());
    }

    /// <summary>
    /// Has a state turn at once, the way the game turns a creature that comes turned as it is switched on: for a creature
    /// that had turned before this game's copy or this game's own object came to show it. The copy does not strike by
    /// itself (GamePatcher.BlackThread).
    /// </summary>
    private static void TurnAtOnce(BlackThreadState state) {
        if (state.IsVisiblyThreaded || !state.gameObject.activeInHierarchy) {
            return;
        }

        Logger.Info($"'{state.name}' is turned at once, as the game that ran it said");
        state.SetVisiblyThreaded();
        state.SetBlackThreadAmount(1f);
        state.StartAttackTest();
    }

    /// <summary>
    /// Has a copy turn as the creature of the game that runs it turns, without its song: the copy shows the song as that
    /// game's creature sings it.
    /// </summary>
    private static void TurnLive(BlackThreadState state) {
        if (state.IsVisiblyThreaded || !state.gameObject.activeInHierarchy) {
            return;
        }

        Logger.Info($"'{state.name}' turns, as the game that runs it said");

        // The game keeps the last turn it started, done or not, and starts none while it keeps one. A turn without the
        // song turns the state the moment it starts, so one kept by a state that is not turned is over: it turned back
        // since.
        state.becomeThreadedRoutine = null;
        var wasApplying = IsApplying;
        IsApplying = true;
        try {
            state.BecomeThreadedNoSing();
        } finally {
            IsApplying = wasApplying;
        }
    }

    /// <summary>
    /// Says that the creature that this game runs struck, for the hook on DoAttack.
    /// </summary>
    /// <param name="state">The state of the creature.</param>
    public void OnStruck(BlackThreadState state) {
        if (IsControlled || state != _states.Host) {
            return;
        }

        if (state.activeAttack != null) {
            var attack = state.activeAttack.transform;
            _strikeScale = attack.localScale;
            _strikeAngle = attack.localEulerAngles.z;
        }

        _strike++;
        Send();
        Logger.Info(
            $"'{state.name}' struck with its third-act attack {_sentAttack} (strike {_strike}), which the other " +
            "players' copies strike too"
        );
    }

    /// <summary>
    /// Says that the creature that this game runs began to darken to strike, for the hook in its attack loop.
    /// </summary>
    /// <param name="state">The state of the creature.</param>
    public void OnDarkens(BlackThreadState state) {
        if (IsControlled || state != _states.Host) {
            return;
        }

        _windUp++;
        Send();
        Logger.Info(
            $"'{state.name}' darkens to strike with its third-act attack {_sentAttack} (wind-up {_windUp}), which the " +
            "other players' copies darken for too"
        );
    }

    /// <summary>
    /// Has a creature that this game runs turn as a blow of another player lands on it, as the game turns a creature
    /// that turns but has not yet as it is hit: the blow came here as health alone, without the hit that the game turns
    /// it from (HealthManagerComponent.DamageLanded). Only the blows of this game's player turned it.
    /// </summary>
    /// <param name="healthManager">The creature's health.</param>
    /// <param name="source">What the blow came from, which is told only for the blows of this game's player.</param>
    public static void OnDamageLanded(HealthManager healthManager, GameObject? source) {
        // A blow of this game's player turns it through the game's own hit
        if (source != null || healthManager == null || healthManager.isDead) {
            return;
        }

        var state = healthManager.GetComponent<BlackThreadState>();
        if (state == null || !RunsHere(state) || !state.isThreaded || state.IsVisiblyThreaded ||
            !state.gameObject.activeInHierarchy) {
            return;
        }

        Logger.Info($"'{state.name}' turns as another player's blow lands on it, as for a blow of this game's player");
        if (state.waitRangeRoutine != null) {
            state.StopCoroutine(state.waitRangeRoutine);
            state.waitRangeRoutine = null;
        }

        state.BecomeThreaded();
    }

    /// <inheritdoc />
    public override void InitializeHost(uint sceneHostEpoch) {
        _isDetermined = true;
        _hasSent = false;
        var tookOver = _ranElsewhere;
        _ranElsewhere = false;

        // The copy's replay of a strike ended as the copy was switched off
        _strikeRoutine = null;
        _hasToldStrike = false;

        // The room's own object that started already goes on as it was as this game takes the creature over, now that
        // it is switched on; one that starts only now does so as it sets up (SetUp). It was off while the other game
        // ran the creature, so nothing it had started runs any more, and the game keeps the last turn and wait it
        // started, done or not, and starts neither while it keeps one.
        var state = _states.Host;
        if (!tookOver || state == null || !state.hasStarted || !state.isBlackThreadWorld ||
            !state.gameObject.activeInHierarchy) {
            return;
        }

        state.becomeThreadedRoutine = null;
        state.waitRangeRoutine = null;
        GoOn(state);
    }

    /// <inheritdoc />
    public override void InitializeClient(uint sceneHostEpoch) {
        _isDetermined = true;
        _ranElsewhere = true;

        // What the game that runs the creature said before it was settled that it does
        if (_isKnown) {
            Follow(_states.Host, atOnce: true);
            Follow(_states.Client, atOnce: true);
        }
    }

    /// <inheritdoc />
    public override void SendAgain() {
        _hasSent = false;
    }

    /// <summary>
    /// Has a followed state that was switched on take what the game that runs the creature said, which it could not
    /// while it was off, for the hook on OnEnable.
    /// </summary>
    public void OnSwitchedOn(BlackThreadState state) {
        if (!IsControlled || !_isKnown || !SetUpHere.Contains(state)) {
            return;
        }

        Follow(state, atOnce: true);
    }

    /// <summary>
    /// Sends what the creature that this game runs is whenever it changes.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        var state = _states.Host;
        if (IsControlled || state == null || !state.isBlackThreadWorld) {
            return;
        }

        var attack = IndexOf(state, state.hasChosenAttack ? state.chosenAttack : null);
        var visible = state.IsVisiblyThreaded;
        if (_hasSent && state.isThreaded == _sentThreaded && visible == _sentVisible && attack == _sentAttack) {
            return;
        }

        Send();
        Logger.Info(
            $"'{state.name}' {(state.isThreaded ? visible ? "turned" : "turns" : "doesn't turn")} in the third act, " +
            $"with attack {attack}, which the other players' copies take"
        );
    }

    /// <summary>
    /// Sends what the creature that this game runs is now, with the number of its last strike.
    /// </summary>
    private void Send() {
        var state = _states.Host;
        _sentThreaded = state.isThreaded;
        _sentVisible = state.IsVisiblyThreaded;
        _sentAttack = IndexOf(state, state.hasChosenAttack ? state.chosenAttack : null);
        _hasSent = true;

        var data = new EntityNetworkData {
            Type = EntityComponentType.BlackThread
        };
        data.Packet.Write((byte) ((_sentThreaded ? 1 : 0) | (_sentVisible ? 2 : 0)));
        data.Packet.Write(_sentAttack);
        data.Packet.Write(_strike);
        data.Packet.Write(_windUp);
        data.Packet.Write(_strikeScale.x);
        data.Packet.Write(_strikeScale.y);
        data.Packet.Write(_strikeAngle);
        SendData(data);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var flags = data.Packet.ReadByte();
        var attack = data.Packet.ReadByte();
        var strike = data.Packet.ReadUShort();
        var windUp = data.Packet.ReadUShort();
        var scale = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        var angle = data.Packet.ReadFloat();

        var wasKnown = _isKnown;
        var struck = wasKnown && !alreadyInSceneUpdate && strike != _strike;
        var woundUp = wasKnown && !alreadyInSceneUpdate && windUp != _windUp;
        _isKnown = true;
        _isThreaded = (flags & 1) != 0;
        _isVisible = (flags & 2) != 0;
        _attack = attack;
        _strike = strike;
        _windUp = windUp;

        if (!wasKnown) {
            Logger.Info(
                $"Told that '{GameObject.Host?.name}' {(_isThreaded ? _isVisible ? "turned" : "turns" : "doesn't turn")} " +
                $"in the third act, with attack {attack}"
            );
        }

        var copy = _states.Client;
        if (copy == null || copy.healthManager == null || !copy.healthManager.isDead) {
            WriteSaved(_isThreaded, "the game that runs it said so");
        }

        // The room's own object is off while the other game runs the creature. The copy shows a turn that it sees
        // happen as it happens, and one from before at once.
        Follow(_states.Host, atOnce: true);
        Follow(copy, atOnce: alreadyInSceneUpdate || !wasKnown);

        // Darkened first, as there; one told only once it struck goes dark at once as it strikes
        if (woundUp && copy != null) {
            StartWindUp(copy, windUp);
        }

        if (struck && copy != null) {
            StartStrike(copy, attack, scale, angle, strike);
        }
    }

    /// <summary>
    /// Has a state that was set up by what the game that runs the creature says take what it says now.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="atOnce">Whether a turn is shown at once, as one that happened before, rather than as it happens.</param>
    private void Follow(BlackThreadState? state, bool atOnce) {
        if (state == null) {
            return;
        }

        if (!SetUpHere.Contains(state)) {
            // The room's own object of this game set itself up, rolling, while this game expected to run the room, and
            // the other game runs it after all: it is set up again by what that game said, once, as the game starts a
            // creature over
            if (state == _states.Host && IsControlled && _isDetermined && state.hasStarted &&
                state.isBlackThreadWorld) {
                Logger.Info(
                    $"'{state.name}' had set up its third-act state by itself before the other game was settled to " +
                    "run it, and is set up again by what that game said"
                );
                SetUpHere.Add(state);
                StartOver(state);
            }

            return;
        }

        // Turned back, or not turning after all: the game starts it over, which sets it up again by what was said
        if (!_isThreaded && state.isThreaded || !_isVisible && state.IsVisiblyThreaded) {
            Logger.Info($"'{state.name}' turns back, as the game that runs it said");
            StartOver(state);
            return;
        }

        if (_isThreaded && !state.isThreaded) {
            state.isThreaded = true;
        }

        if (!state.isThreaded) {
            return;
        }

        TakeAttack(state);
        if (_isVisible && !state.IsVisiblyThreaded) {
            if (atOnce) {
                TurnAtOnce(state);
            } else {
                TurnLive(state);
            }
        }
    }

    /// <summary>
    /// Has a followed state start over the way the game starts a creature over, which sets it up again by what the game
    /// that runs the creature said (see <see cref="SetUp"/>). Everything it had started ends there, and the game keeps
    /// the last turn and wait it started, done or not, and starts neither while it keeps one: they are let go of, so
    /// that it can turn again.
    /// </summary>
    private void StartOver(BlackThreadState state) {
        if (state == _states.Client) {
            _strikeRoutine = null;
            _hasToldStrike = false;
        }

        state.becomeThreadedRoutine = null;
        state.waitRangeRoutine = null;
        var wasApplying = IsApplying;
        IsApplying = true;
        try {
            state.ResetThreaded();
        } finally {
            IsApplying = wasApplying;
        }
    }

    /// <summary>
    /// Has a state strike with the attack that the game that runs it chose.
    /// </summary>
    private void TakeAttack(BlackThreadState state) {
        if (AttackAt(state, _attack) is { } attack) {
            state.chosenAttack = attack;
            state.hasChosenAttack = true;
        }
    }

    /// <summary>
    /// Whether the copy shows the creature able to strike - turned, as only a turned creature strikes, switched on and
    /// alive - and why not, for the log.
    /// </summary>
    private static bool CanStrike(BlackThreadState copy, out string whyNot) {
        var dead = copy.healthManager != null && copy.healthManager.isDead;
        if (copy.isThreaded && copy.IsVisiblyThreaded && copy.gameObject.activeInHierarchy && !dead) {
            whyNot = "";
            return true;
        }

        whyNot = dead ? "it is dead" : !copy.gameObject.activeInHierarchy ? "it is off" : "it is not turned here";
        return false;
    }

    /// <summary>
    /// Ends what the copy does of a strike, darkening or striking, for a new one: the creature of the game that runs it
    /// strikes one at a time.
    /// </summary>
    private void StopStrike(BlackThreadState copy) {
        if (_strikeRoutine != null) {
            copy.StopCoroutine(_strikeRoutine);
            _strikeRoutine = null;
        }

        copy.CancelAttack();
    }

    /// <summary>
    /// Has the copy darken to strike as the creature of the game that runs it began to.
    /// </summary>
    private void StartWindUp(BlackThreadState copy, ushort windUp) {
        if (!CanStrike(copy, out var whyNot)) {
            Logger.Info(
                $"'{copy.name}' does not darken as the creature of the game that runs it did (wind-up {windUp}): " +
                whyNot
            );
            return;
        }

        StopStrike(copy);
        _strikeRoutine = copy.StartCoroutine(WindUp(copy, windUp));
    }

    /// <summary>
    /// Darkens as the creature of the game that runs it darkens to strike, over the same while, as the game darkens a
    /// creature from its colours to black. That game strikes the moment it is dark, which comes as a strike
    /// (see <see cref="Strike"/>); one that did not strike after all, as without the attack's object, lightens again
    /// after the time of its attack, as there.
    /// </summary>
    private IEnumerator WindUp(BlackThreadState state, ushort windUp) {
        Logger.Info($"'{state.name}' darkens to strike (wind-up {windUp}), as the creature of the game that runs it does");
        state.StopPulseRoutine();
        var sprites = state.tk2dSprites;
        var startColors = state.startColors;
        var count = Mathf.Min(sprites.Length, startColors.Length);
        for (var i = 0; i < count; i++) {
            startColors[i] = sprites[i].color;
        }

        var duration = GlobalSettings.Effects.BlackThreadEnemyAttackTintDuration;
        var curve = GlobalSettings.Effects.BlackThreadEnemyAttackTintCurve;
        for (var elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime) {
            var amount = curve.Evaluate(elapsed / duration);
            for (var i = 0; i < count; i++) {
                sprites[i].color = Color.Lerp(startColors[i], Color.black, amount);
            }

            yield return null;
        }

        foreach (var sprite in sprites) {
            sprite.color = Color.black;
        }

        var attack = AttackAt(state, _attack) ?? state.chosenAttack;
        var attackTime = attack != null ? attack.Duration : 0f;
        for (var waited = 0f; waited < attackTime; waited += Time.deltaTime) {
            yield return null;
        }

        Logger.Info($"'{state.name}' was told of no strike after it darkened (wind-up {windUp}), and lightens again");
        for (var elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime) {
            Lighten(state, curve.Evaluate(elapsed / duration));
            yield return null;
        }

        Lighten(state, 1f);
        state.StartPulseRoutine();
        _strikeRoutine = null;
    }

    /// <summary>
    /// Has the copy strike as the creature of the game that runs it struck, ending a strike of its own that is still
    /// going on.
    /// </summary>
    private void StartStrike(BlackThreadState copy, byte attackIndex, Vector2 scale, float angle, ushort strike) {
        if (!CanStrike(copy, out var whyNot)) {
            Logger.Info(
                $"'{copy.name}' does not strike as the creature of the game that runs it did (strike {strike}): " +
                whyNot
            );
            return;
        }

        StopStrike(copy);
        _strikeRoutine = copy.StartCoroutine(Strike(copy, AttackAt(copy, attackIndex), scale, angle, strike));
    }

    /// <summary>
    /// Strikes as the creature of the game that runs it struck: dark, which it is already when it darkened as that
    /// creature did (see <see cref="WindUp"/>) and goes at once otherwise; the attack sits the way it sat there, and it
    /// lightens again after.
    /// </summary>
    private IEnumerator Strike(BlackThreadState state, BlackThreadAttack? attack, Vector2 scale, float angle,
        ushort strike) {
        attack ??= state.chosenAttack;
        if (attack == null) {
            Logger.Info($"'{state.name}' has no third-act attack to strike with (strike {strike})");
            _strikeRoutine = null;
            yield break;
        }

        Logger.Info($"'{state.name}' strikes with its third-act attack (strike {strike}), as the game that runs it did");
        state.StopPulseRoutine();
        var sprites = state.tk2dSprites;
        foreach (var sprite in sprites) {
            sprite.color = Color.black;
        }

        _hasToldStrike = true;
        _toldStrikeScale = scale;
        _toldStrikeAngle = angle;
        var wasApplying = IsApplying;
        IsApplying = true;
        try {
            state.DoAttack(attack);
        } finally {
            IsApplying = wasApplying;
        }

        // Put the way it sat as the attack was switched on (GamePatcher.BlackThread), or else now
        ApplyToldStrike(state);
        var shown = state.activeAttack;

        yield return new WaitForSecondsInterruptable(attack.Duration, state.attackEndedFunc, false);
        if (state.activeAttack == shown) {
            state.activeAttack = null;
        }

        var duration = GlobalSettings.Effects.BlackThreadEnemyAttackTintDuration;
        var curve = GlobalSettings.Effects.BlackThreadEnemyAttackTintCurve;
        for (var elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime) {
            Lighten(state, curve.Evaluate(elapsed / duration));
            yield return null;
        }

        Lighten(state, 1f);
        state.StartPulseRoutine();
        _strikeRoutine = null;
    }

    /// <summary>
    /// Colours a state from black back towards its own colours by an amount, as the game lightens a creature after it
    /// struck.
    /// </summary>
    private static void Lighten(BlackThreadState state, float amount) {
        var sprites = state.tk2dSprites;
        var colors = state.initialColors;
        var count = Mathf.Min(sprites.Length, colors.Length);
        for (var i = 0; i < count; i++) {
            sprites[i].color = Color.Lerp(Color.black, colors[i], amount);
        }
    }

    /// <summary>
    /// Puts the attack of the strike that the copy replays the way it sat on the creature of the game that runs it,
    /// once, as the attack is switched on, for the hook on DoAttack: the game turned it towards this game's player.
    /// </summary>
    public void ApplyToldStrike(BlackThreadState state) {
        if (!_hasToldStrike) {
            return;
        }

        _hasToldStrike = false;
        if (state.activeAttack == null) {
            return;
        }

        var transform = state.activeAttack.transform;
        transform.localScale = new Vector3(_toldStrikeScale.x, _toldStrikeScale.y, transform.localScale.z);
        var euler = transform.localEulerAngles;
        transform.localEulerAngles = new Vector3(euler.x, euler.y, _toldStrikeAngle);
    }

    /// <summary>
    /// Writes into this game's save whether the creature turns, the way the game keeps it until the player rests.
    /// </summary>
    private void WriteSaved(bool threaded, string why) {
        var roomObject = GameObject.Host;
        var sceneData = SceneData.instance;
        if (roomObject == null || sceneData == null) {
            return;
        }

        var scene = roomObject.scene.name;
        var id = roomObject.name + SavedIdSuffix;
        if (sceneData.PersistentBools.TryGetValue(scene, id, out var saved) && saved.Value == threaded) {
            return;
        }

        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            SceneName = scene,
            ID = id,
            Value = threaded,
            IsSemiPersistent = true
        });
        Logger.Info($"Wrote down that '{roomObject.name}' {(threaded ? "turns" : "doesn't turn")} here, as {why}");
    }

    /// <summary>
    /// The attacks that a state strikes with, the way the game picks from them.
    /// </summary>
    private static BlackThreadAttack[]? AttacksOf(BlackThreadState state) {
        return state.attacks is { Length: > 0 } ? state.attacks : GlobalSettings.Effects.BlackThreadAttacksDefault;
    }

    /// <summary>
    /// Which of its attacks a state strikes with, or <see cref="NoAttack"/>.
    /// </summary>
    private static byte IndexOf(BlackThreadState state, BlackThreadAttack? attack) {
        var attacks = AttacksOf(state);
        if (attack == null || attacks == null) {
            return NoAttack;
        }

        var index = System.Array.IndexOf(attacks, attack);
        return index is >= 0 and < NoAttack ? (byte) index : NoAttack;
    }

    /// <summary>
    /// The attack of a state at an index, or null.
    /// </summary>
    private static BlackThreadAttack? AttackAt(BlackThreadState state, byte index) {
        var attacks = AttacksOf(state);
        return attacks != null && index < attacks.Length ? attacks[index] : null;
    }

    /// <inheritdoc />
    public override void Destroy() {
        _strikeRoutine = null;
        foreach (var state in new[] { _states.Host, _states.Client }) {
            if (state is not null && ByState.TryGetValue(state, out var component) && component == this) {
                ByState.Remove(state);
                SetUpHere.Remove(state);
            }
        }
    }
}
