using System;
using System.Collections.Generic;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What the third act does to a creature that every game runs by itself (see OwnCreatures): whether it is one that
/// turns, which of its attacks it strikes with, when it turns and when it strikes. The game decides all of it in each
/// game by itself: whether it turns by a roll and by what the save of that game kept, which attack by another roll,
/// when it turns by its own player staying near it or striking it, when it strikes by its own player standing in its
/// reach and by one more roll. Each game had its own creature turn or not, and strike at its own times, the first
/// kind that came over among them (USER 11-16, of the crab, "先把1做了？我们可以慢慢来"). Rolling under the creature's
/// name, as its moves do, gives no same answer here: the roll of whether it turns goes by how often each answer came
/// up of late for every creature of that game, and the save that keeps what came out is each player's own.
///
/// So the scene host's game decides, as the game that runs a creature decides for its copies (see
/// BlackThreadComponent; USER 10-31 "就按照以前我们怎么做这类掷骰子的方法修"): it sets the creature up as the game
/// does, turns it as any player stays near it or strikes it, and has it strike whom it goes after as they stand in
/// its reach (GamePatcher.BlackThread), and each time the members in the room are told. Every other game sets the
/// creature up the way it was told, by the game's own setup, which takes the answer from the save, and turns it and has
/// it strike by the game's own steps as it is told to: its song, its darkening and its attack come from its own state
/// machines and loop, and the strike by the game's own way of queueing one. A creature that turns, or not, by no roll -
/// one the room has turned from the start, or one that something else turns - is set up so in every game at once;
/// which attack it strikes with is told all the same. Until it is settled whose game is the scene host's, a creature
/// that rolls waits to be set up, as either game could turn out to be the one that decides.
/// </summary>
internal static class OwnThreads {
    /// <summary>
    /// The number that a word about the third-act state of a creature goes under (CoopSaveUpdate.PartCount), beside
    /// the words of its steps and of its death.
    /// </summary>
    public const ushort WordPart = 3;

    /// <summary>
    /// What a word says, the first of its amounts; the second holds the flags below, the third the attack.
    /// </summary>
    private enum Word {
        /// <summary>
        /// What the creature is: whether it turns, whether it turned, and which attack it strikes with.
        /// </summary>
        Is = 0,

        /// <summary>
        /// It begins to turn, with its song or without.
        /// </summary>
        Turns = 1,

        /// <summary>
        /// Its attack loop chose to strike.
        /// </summary>
        Strikes = 2
    }

    /// <summary>
    /// The flags of a word: whether it turns, whether it turned or began to, and whether it turns with its song.
    /// </summary>
    private const int ThreadedFlag = 1, TurnedFlag = 2, SongFlag = 4;

    /// <summary>
    /// What is known here of the third-act state of each creature that every game runs by itself.
    /// </summary>
    private static readonly Dictionary<BlackThreadState, OwnThread> Threads = new();

    /// <summary>
    /// Sends a word to the members in the room, set by CoopSave.
    /// </summary>
    public static Action<CoopSaveUpdate>? SendToRoomMembers { get; set; }

    /// <summary>
    /// Sets up the third-act state of a creature that every game runs by itself, for the hook on SetupThreaded: the
    /// scene host's game as the game does, every other game by what it was told, and a creature that rolls in no game
    /// until it is settled whose game decides.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="isFirst">Whether it is the first time, as the creature starts.</param>
    /// <param name="orig">The game's own setup.</param>
    /// <returns>Whether it is the state of such a creature, which was seen to here.</returns>
    public static bool SetUp(BlackThreadState state, bool isFirst, Action<BlackThreadState, bool> orig) {
        if (!OwnCreatures.Has(state.gameObject)) {
            return false;
        }

        // What the game leaves as it is
        if (!state.isBlackThreadWorld || state.isThreaded || state.singFsm == null) {
            orig(state, isFirst);
            return true;
        }

        // Set up again, as the game sets it up as it turns it back, it is no longer turned
        var role = OwnCreatures.SceneRole?.Invoke();
        var thread = ThreadOf(state);
        if (!isFirst) {
            thread.Turned = false;
        }

        if (!Rolls(state)) {
            orig(state, isFirst);
            AfterSetUp(state, thread, role);
            return true;
        }

        if (role == null || role == false && !thread.Known) {
            if (thread.Waits) {
                thread.WaitsFirst |= isFirst;
                return true;
            }

            thread.Waits = true;
            thread.WaitsFirst = isFirst;
            thread.HealthBefore = state.healthManager.hp;
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(state.gameObject)}' waits to set up its third-act state " +
                (role == null ? "until it is settled whose game is the scene host's" : "until the scene host's says")
            );
            return true;
        }

        // The blows that it took as it waited came off the health that it starts with, which the game doubles for one
        // that turns, as they did in the scene host's game, which waited less or not at all. The health of the scene
        // host's creature, if it was told meanwhile, is the creature's as it is
        var health = state.healthManager;
        var waited = thread.Waits;
        var told = waited && thread.HealthTold;
        var hp = health.hp;
        if (waited) {
            thread.Waits = false;
            thread.HealthTold = false;
            isFirst |= thread.WaitsFirst;
            if (!told) {
                health.hp = thread.HealthBefore;
            }
        }

        // The game's own setup takes what the save keeps rather than rolling, and the save keeps it until the player
        // rests, so that this game starts it the same way if it runs the room later
        if (role == false) {
            BlackThreadComponent.SaveThreaded(state.gameObject, thread.Threaded, "the scene host's game said so");
        }

        orig(state, isFirst);
        if (told) {
            health.hp = hp;
        } else if (waited) {
            health.hp -= thread.HealthBefore - hp;
        }

        AfterSetUp(state, thread, role);
        return true;
    }

    /// <summary>
    /// Whether the game sets a creature up by a roll: not one that comes turned by being forced to, by a core of the
    /// third act or by something that turns it later, which the game turns in every game alike.
    /// </summary>
    private static bool Rolls(BlackThreadState state) {
        return !state.force && !BlackThreadCore.IsAnyActive && !state.willBeThreaded;
    }

    /// <summary>
    /// What follows the game's own setup of a creature: the scene host's game tells what came out, and another game
    /// that was told takes the attack it strikes with, and turns it at once if it turned there already.
    /// </summary>
    private static void AfterSetUp(BlackThreadState state, OwnThread thread, bool? role) {
        if (role == true) {
            Tell(state, Word.Is, false, "it set up its third-act state");
        } else if (thread.Known) {
            Follow(state, thread);
        }
    }

    /// <summary>
    /// Has a creature that was set up take what the scene host's game said it is: the attack it strikes with, and,
    /// if it turned there before this game came to show it, turned at once, the way the game has a creature that comes
    /// turned go on.
    /// </summary>
    private static void Follow(BlackThreadState state, OwnThread thread) {
        if (!state.isThreaded) {
            return;
        }

        // One that the room has turned from the start comes turned here already
        BlackThreadComponent.Choose(state, thread.Attack);
        if (!thread.Turned || state.IsVisiblyThreaded || state.becomeThreadedRoutine != null ||
            state.startAlreadyBlackThreadedOnEnable || !state.gameObject.activeInHierarchy || IsDead(state)) {
            return;
        }

        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(state.gameObject)}' turned already in the scene host's game, " +
            "and comes turned here"
        );
        StopWaiting(state);
        state.StartCoroutine(state.WaitForActive());
        state.SetBlackThreadAmount(1f);
        state.StartAttackTest();
    }

    /// <summary>
    /// Ends the wait of a creature for a player to stay near it, as the game ends it as the creature turns otherwise.
    /// </summary>
    private static void StopWaiting(BlackThreadState state) {
        if (state.waitRangeRoutine != null) {
            state.StopCoroutine(state.waitRangeRoutine);
            state.waitRangeRoutine = null;
        }
    }

    /// <summary>
    /// Tells the members in the room that a creature of the scene host's game begins to turn, for the hooks on the
    /// two ways the game turns it, before the game starts the turn: the game starts none while it keeps one.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="sing">Whether it turns with its song.</param>
    public static void OnTurn(BlackThreadState state, bool sing) {
        if (state.becomeThreadedRoutine != null || !state.gameObject.activeInHierarchy ||
            !OwnCreatures.Has(state.gameObject) || OwnCreatures.SceneRole?.Invoke() != true) {
            return;
        }

        ThreadOf(state).Turned = true;
        Tell(state, Word.Turns, sing, sing ? "it begins to turn, with its song" : "it begins to turn");
    }

    /// <summary>
    /// Whether a blow counts for the third-act state of a creature, for the hook on the game's answer to a blow, given
    /// whether the creature turns: one that turns but has not yet turns at a blow in the scene host's game alone, which
    /// tells the others, as every game takes every blow and the scene host's decides when it turns.
    /// </summary>
    /// <param name="threaded">Whether the creature turns.</param>
    /// <param name="state">The state.</param>
    public static bool HitCounts(bool threaded, BlackThreadState state) {
        if (!threaded || state.IsVisiblyThreaded || !OwnCreatures.Has(state.gameObject) ||
            OwnCreatures.SceneRole?.Invoke() == true) {
            return threaded;
        }

        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(state.gameObject)}' was struck before it turned, which it turns " +
            "for as the scene host's game says"
        );
        return false;
    }

    /// <summary>
    /// Whether the attack loop of a creature strikes, for the hook on its roll after it found whom it goes after in
    /// reach: in the scene host's game as the roll came out, which tells the others; in every other game never by
    /// itself, as it strikes when it is told to.
    /// </summary>
    /// <param name="rolled">What the roll came to.</param>
    /// <param name="state">The state.</param>
    public static bool Decides(bool rolled, BlackThreadState state) {
        if (!OwnCreatures.Has(state.gameObject)) {
            return rolled;
        }

        if (OwnCreatures.SceneRole?.Invoke() != true) {
            return false;
        }

        if (rolled) {
            Tell(state, Word.Strikes, false, "it strikes with its third-act attack");
        }

        return rolled;
    }

    /// <summary>
    /// How near a creature measures the player it waits for to stay near before it turns, for the hook on its wait: in
    /// every game but the scene host's, nobody is ever near, as it turns when it is told to.
    /// </summary>
    /// <param name="sqrDistance">How near the player stands, squared.</param>
    /// <param name="state">The state.</param>
    public static float Nearness(float sqrDistance, BlackThreadState state) {
        return OwnCreatures.Has(state.gameObject) && OwnCreatures.SceneRole?.Invoke() != true
            ? float.PositiveInfinity
            : sqrDistance;
    }

    /// <summary>
    /// The word of what a creature is, for a member who comes into the room, or null for one that was not set up yet.
    /// </summary>
    public static CoopSaveUpdate? WordFor(GameObject creature) {
        if (!creature.TryGetComponent<BlackThreadState>(out var state) || !state.hasStarted ||
            !state.isBlackThreadWorld || Threads.TryGetValue(state, out var thread) && thread.Waits) {
            return null;
        }

        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' {Describe(state)} here, which the member who came " +
            "in is told"
        );
        return WordOf(state, Word.Is, false);
    }

    /// <summary>
    /// Takes a word of the scene host's game about the third-act state of a creature, out yet here or not. Every word
    /// says what the creature is there now, with the attack it strikes with, so that what one that came out of turn,
    /// or found the creature not set up here yet or off, missed is made up for: a creature whose setup waits is set up
    /// by it, and one that was off or missed its turn there comes turned as it can.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="update">The word.</param>
    /// <param name="from">The name of the player whose game it came from.</param>
    public static void Take(GameObject creature, CoopSaveUpdate update, string from) {
        var place = OwnCreatures.PlaceOf(creature);
        if (!creature.TryGetComponent<BlackThreadState>(out var state) || update.Amounts.Count < 3) {
            Logger.Info($"[Own creatures] {from}'s game told of the third-act state of '{place}', which has none here");
            return;
        }

        if (OwnCreatures.SceneRole?.Invoke() == true) {
            Logger.Info(
                $"[Own creatures] {from}'s game told of the third-act state of '{place}', which this game decides"
            );
            return;
        }

        var word = (Word) update.Amounts[0];
        var flags = update.Amounts[1];
        var thread = ThreadOf(state);
        thread.Known = true;
        thread.Threaded = (flags & ThreadedFlag) != 0;
        thread.Turned |= (flags & TurnedFlag) != 0;
        thread.Attack = (byte) update.Amounts[2];
        Logger.Info(
            $"[Own creatures] '{place}' " +
            (word == Word.Turns ? "begins to turn" : word == Word.Strikes ? "strikes" : "is set up") +
            $" in {from}'s game, where it " + (thread.Threaded ? thread.Turned ? "turned" : "turns" : "doesn't turn") +
            $" in the third act, with attack {thread.Attack}"
        );

        if (thread.Waits) {
            Resume(state, thread);
            return;
        }

        // One that has not started yet is set up by it as it starts
        if (!state.hasStarted || !state.isBlackThreadWorld) {
            return;
        }

        switch (word) {
            case Word.Is:
                TakeWhatItIs(state, thread, place);
                break;
            case Word.Turns:
                TakeTurn(state, thread, (flags & SongFlag) != 0, place);
                break;
            case Word.Strikes:
                TakeStrike(state, thread, place);
                break;
        }
    }

    /// <summary>
    /// Takes what a creature that was set up already is in the scene host's game: the save keeps it, and the creature
    /// takes the attack, and turns at once if it turned there.
    /// </summary>
    private static void TakeWhatItIs(BlackThreadState state, OwnThread thread, string place) {
        if (Rolls(state)) {
            BlackThreadComponent.SaveThreaded(state.gameObject, thread.Threaded, "the scene host's game said so");
        }

        if (state.isThreaded != thread.Threaded) {
            Logger.Info(
                $"[Own creatures] '{place}' was set up as one that {(state.isThreaded ? "turns" : "doesn't turn")} " +
                "here, otherwise than there"
            );
        }

        Follow(state, thread);
    }

    /// <summary>
    /// Has a creature turn as it began to in the scene host's game, the way the game turns it there, with its song or
    /// without. One that is off here comes out turned (see <see cref="OnSwitchedOn"/>).
    /// </summary>
    private static void TakeTurn(BlackThreadState state, OwnThread thread, bool sing, string place) {
        if (state.isThreaded) {
            BlackThreadComponent.Choose(state, thread.Attack);
        }

        string? whyNot = !state.isThreaded ? "it was not set up as one that turns here"
            : state.IsVisiblyThreaded || state.becomeThreadedRoutine != null ? "it turns here already"
            : !state.gameObject.activeInHierarchy ? "it is off here, and comes out turned"
            : null;
        if (whyNot != null) {
            Logger.Info($"[Own creatures] '{place}' does not begin to turn here: {whyNot}");
            return;
        }

        Logger.Info($"[Own creatures] '{place}' turns{(sing ? ", with its song," : "")} as in the scene host's game");
        StopWaiting(state);
        if (sing) {
            state.BecomeThreaded();
        } else {
            state.BecomeThreadedNoSing();
        }
    }

    /// <summary>
    /// Has a creature strike as its attack loop chose to in the scene host's game: its own loop strikes at once, the
    /// way it strikes at an attack that the game queues, by its own song, darkening and attack. One that missed the
    /// turn there comes turned first.
    /// </summary>
    private static void TakeStrike(BlackThreadState state, OwnThread thread, string place) {
        Follow(state, thread);
        var turns = state.isThreaded && (state.IsVisiblyThreaded || state.becomeThreadedRoutine != null);
        string? whyNot = IsDead(state) ? "it is dead here"
            : !state.gameObject.activeInHierarchy ? "it is off here"
            : !turns ? "it is not turned here"
            : null;
        if (whyNot != null) {
            Logger.Info($"[Own creatures] '{place}' does not strike here: {whyNot}");
            return;
        }

        Logger.Info($"[Own creatures] '{place}' strikes with its third-act attack as in the scene host's game");
        state.SetAttackQueued(true);
    }

    /// <summary>
    /// Whether a creature is dead.
    /// </summary>
    private static bool IsDead(BlackThreadState state) {
        return state.healthManager != null && state.healthManager.isDead;
    }

    /// <summary>
    /// Sets up the creatures that waited to be set up until it was settled whose game is the scene host's: the scene
    /// host's game as the game does, and another game by what it was told, or later as it is told.
    /// </summary>
    public static void OnRoleSettled() {
        if (Threads.Count == 0) {
            return;
        }

        foreach (var pair in new List<KeyValuePair<BlackThreadState, OwnThread>>(Threads)) {
            if (pair.Key != null) {
                Resume(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>
    /// Has a creature that comes out again take what it missed while it was off, for the hook on OnEnable: one whose
    /// setup waited is set up, and one that turned in the scene host's game meanwhile comes turned.
    /// </summary>
    public static void OnSwitchedOn(BlackThreadState state) {
        if (Threads.Count == 0 || !Threads.TryGetValue(state, out var thread)) {
            return;
        }

        if (thread.Waits) {
            Resume(state, thread);
        } else if (thread.Known && state.hasStarted && state.isBlackThreadWorld &&
                   OwnCreatures.SceneRole?.Invoke() == false) {
            Follow(state, thread);
        }
    }

    /// <summary>
    /// Notes that a creature took the health that it has in the scene host's game, as a member who comes in does,
    /// for CoopSave.OwnCreatures: that health has the game's doubling of one that turns in it already, which a setup
    /// that waits here then leaves as it is.
    /// </summary>
    public static void TookHealth(GameObject creature) {
        if (Threads.Count > 0 && creature.TryGetComponent<BlackThreadState>(out var state) &&
            Threads.TryGetValue(state, out var thread) && thread.Waits) {
            thread.HealthTold = true;
        }
    }

    /// <summary>
    /// Sets up a creature whose setup waited, if it can be now, as the game sets a creature up: as it starts, out and
    /// alive. One that is off is set up as it comes out again (see <see cref="OnSwitchedOn"/>), and one that died
    /// never.
    /// </summary>
    private static void Resume(BlackThreadState state, OwnThread thread) {
        if (thread.Waits && state.isActiveAndEnabled &&
            !(state.healthManager != null && state.healthManager.isDead)) {
            state.SetupThreaded(thread.WaitsFirst);
        }
    }

    /// <summary>
    /// Forgets what is known, as the room is left.
    /// </summary>
    public static void Clear() {
        Threads.Clear();
    }

    /// <summary>
    /// What is known here of the third-act state of a creature.
    /// </summary>
    private static OwnThread ThreadOf(BlackThreadState state) {
        if (!Threads.TryGetValue(state, out var thread)) {
            thread = new OwnThread();
            Threads[state] = thread;
        }

        return thread;
    }

    /// <summary>
    /// Tells the members in the room of a creature of the scene host's game.
    /// </summary>
    private static void Tell(BlackThreadState state, Word word, bool sing, string what) {
        if (SendToRoomMembers is not { } send) {
            return;
        }

        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(state.gameObject)}' {Describe(state)}: {what}, as the members " +
            "in the room are told"
        );
        send(WordOf(state, word, sing));
    }

    /// <summary>
    /// A word about a creature, as it is now.
    /// </summary>
    private static CoopSaveUpdate WordOf(BlackThreadState state, Word word, bool sing) {
        var turned = state.IsVisiblyThreaded || Threads.TryGetValue(state, out var thread) && thread.Turned;
        var flags = (state.isThreaded ? ThreadedFlag : 0) | (turned ? TurnedFlag : 0) | (sing ? SongFlag : 0);
        return new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = state.gameObject.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(state.gameObject),
            PartCount = WordPart,
            Amounts = [(int) word, flags, AttackOf(state)]
        };
    }

    /// <summary>
    /// What a creature is, for the log.
    /// </summary>
    private static string Describe(BlackThreadState state) {
        return (state.isThreaded ? state.IsVisiblyThreaded ? "turned" : "turns" : "doesn't turn") +
               $" in the third act, with attack {AttackOf(state)}";
    }

    /// <summary>
    /// Which of its attacks a creature strikes with (see BlackThreadComponent.IndexOf).
    /// </summary>
    private static byte AttackOf(BlackThreadState state) {
        return BlackThreadComponent.IndexOf(state, state.hasChosenAttack ? state.chosenAttack : null);
    }

    /// <summary>
    /// What is known here of the third-act state of a creature that every game runs by itself.
    /// </summary>
    private sealed class OwnThread {
        /// <summary>
        /// Whether the scene host's game said what it is, and what: whether it turns, whether it turned or began to,
        /// and which attack it strikes with. In the scene host's game, whether it began to turn there.
        /// </summary>
        public bool Known, Threaded, Turned;

        /// <inheritdoc cref="Known"/>
        public byte Attack = BlackThreadComponent.NoAttack;

        /// <summary>
        /// Whether its setup waits, and whether it waits for the first, as the creature starts.
        /// </summary>
        public bool Waits, WaitsFirst;

        /// <summary>
        /// Its health as it began to wait, and whether it took the health of the scene host's creature meanwhile.
        /// </summary>
        public int HealthBefore;

        /// <inheritdoc cref="HealthBefore"/>
        public bool HealthTold;
    }
}
