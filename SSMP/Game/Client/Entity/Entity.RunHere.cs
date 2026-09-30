using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Entity.Component;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What the local player does to the copy of an entity, played at once in their own game by the copy's own FSM: a
/// strike on a juggled flea or a falling bell, a rock that bursts on the player it touches. Only for entities whose
/// registry entry says so (see <see cref="EntityRegistryEntry.LocalFirst"/>), and for any creature one of whose parts
/// catches the local player for a combo of blows (see <see cref="CatchEvents"/>).
///
/// The copy runs none of its FSMs, so what the player did went to the scene host and came back a round trip later,
/// while the flea fell on through the player's nail. Now the game of the player who did it runs the copy's own FSM from
/// the state the scene host says it is in: the event, and every state it goes through at once from there. What that
/// does to the entity itself - how it moves, looks and sounds - happens here the way it happens in the scene host's
/// game. What it does to anything both games share - a point told to the room, the save, a creature - is left to the
/// scene host, which does it once for both (see <see cref="IsLeftToSceneHost"/>). What it does to the local player -
/// the hold and the slashes of a catch - is theirs, and runs here only. The scene host is sent where the copy was, how
/// it moved and the dice that the FSM rolled; it puts its entity there, plays the same event with the same dice,
/// leaving out what it would do to its own player (see <see cref="PlayForPartner"/>), and carries it on by the time
/// that took to arrive (see <see cref="TakeInput"/>). In the same breath it answers with the number the input went
/// under and the state its FSM went to: the echo (see <see cref="HearEcho"/>).
///
/// Until the echo comes, what the scene host sends of that FSM, of how the entity moves by itself and of what it plays
/// is from before, and is held. If the scene host's FSM went where the copy's went, all of that is dropped, since the
/// copy has done it; if not - the flea had already landed over there - the copy follows the scene host again at once,
/// with all of it. Either way the copy's FSM goes to no other state by itself after the input: where the entity goes
/// next is the scene host's to say, and the copy follows it again as soon as it says anything more of that FSM. Run on
/// by itself, the copy would do each of those states a moment before the scene host does, and again when the scene
/// host's game sends them. A catch is the one input that goes on further: the states that only the catch leads to are
/// the combo it starts, all of it about the player caught, so the copy plays them by itself and takes none of them
/// from the scene host, which leaves the player's part of them out (see <see cref="_runHereCombo"/>).
///
/// A catch that one of the copy's parts felt for itself - a tendril that grabs the player, a charge that seizes them
/// to drain their silk, a maw that takes a thing of theirs - goes further in another way: the game of the player it
/// caught leads it. What the creature does with them from there is theirs to decide: whether they struggle free,
/// whether they are eaten, how much silk they have left to be drained. Played by the scene host by itself, without
/// them, it waited for struggles that nobody made and chewed on nobody for good. So the copy tells the scene host each
/// state it goes to, and the scene host's FSM goes there too and nowhere else, but out of the catch by its own way - its
/// death, a stun, the catch ending by its own clock (see <see cref="TakeCatchState"/>).
///
/// The end of a boss goes on further still, and for good: from the state that its registry entry names, each game
/// plays the rest of that FSM for its own player (see <see cref="RunEachGamePart"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The most times that an FSM waiting for the next frame is stepped on at once (see <see cref="Settle"/>).
    /// </summary>
    private const int MaxSettleSteps = 8;

    /// <summary>
    /// The state index in an echo that says the input led nowhere from where the scene host's FSM was.
    /// </summary>
    private const byte NotTaken = byte.MaxValue;

    /// <summary>
    /// How far an eased movement has got, in seconds, which the scene host moves on to carry an input on.
    /// </summary>
    private static readonly FieldInfo? EaseRunningTimeField = typeof(EaseFsmAction).GetField(
        "runningTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The frame that an action waiting for the next frame started on, which is moved back to step it on at once.
    /// </summary>
    private static readonly FieldInfo? NextFrameEnterField = typeof(NextFrameEvent).GetField(
        "enterFrameCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The state an FSM is about to go to, which is cleared to keep it where it is (see <see cref="OnSwitchState"/>).
    /// </summary>
    private static readonly FieldInfo? SwitchToStateField = typeof(HutongGames.PlayMaker.Fsm).GetField(
        "switchToState", BindingFlags.Instance | BindingFlags.NonPublic
    );

    /// <summary>
    /// The FSMs of copies that run here, with their entities, which go to no other state unless an input of the local
    /// player takes them there or it is one of the combo of a catch (see <see cref="PlayHere"/>).
    /// </summary>
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, Entity> HeldFsms = new();

    /// <summary>
    /// The hook that keeps the FSMs in <see cref="HeldFsms"/> where they are, and those in <see cref="LedFsms"/> on the
    /// way the partner's game leads them, put in place the first time either is needed.
    /// </summary>
    private static Hook? _switchStateHook;

    /// <summary>
    /// The FSM of a copy that plays an input of the local player right now, whose way through the states is noted for
    /// a catch that this game leads (see <see cref="PlayHere"/>), or null.
    /// </summary>
    private static HutongGames.PlayMaker.Fsm? _recordedFsm;

    /// <summary>
    /// The states that <see cref="_recordedFsm"/> has gone to so far, in order.
    /// </summary>
    private static List<FsmState>? _recordedPath;

    /// <summary>
    /// The FSMs of the room's own objects that play a catch of the partner's which the partner's game leads, with what
    /// they follow (see <see cref="TakeCatchState"/>).
    /// </summary>
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, Lead> LedFsms = new();

    /// <summary>
    /// How long an FSM that plays a catch of the partner's which their game leads goes on waiting to hear where it
    /// goes next, in seconds, before it lets go by itself (see <see cref="UpdateLeads"/>). A creature that holds a
    /// player changes state every few moments while it does - each struggle, each bite - so this only runs out when the
    /// partner's game has stopped saying anything: it left the room, or the connection dropped.
    /// </summary>
    private const float LeadTimeout = 15f;

    /// <summary>
    /// Raised on a scene client when the FSM of a copy that runs here for a catch this game leads goes to another state
    /// of the catch, or would go out of it (see <see cref="PlayHere"/>): the entity, the index of the FSM, the number
    /// that the catch went under, the number of this step of it, counting from one after the input, and the state.
    /// </summary>
    public static event Action<Entity, byte, byte, byte, string>? CatchWentOn;

    /// <summary>
    /// The kinds of action, by name, whose effect reaches beyond the entity to what both games share, which a copy that
    /// runs here leaves to the scene host (see <see cref="IsLeftToSceneHost"/>).
    /// </summary>
    private static readonly HashSet<string> SharedEffectActionNames = [
        // Told to the whole room, and counted once: a point, a flea that got past
        "SendEventToRegister", "SendEventToRegisterV2", "SendEventToRegisterDelay",
        // Written into another FSM, like the tally of a game
        "SetFsmBool", "SetFsmFloat", "SetFsmInt", "SetFsmString",
        // Kept in the save
        "SetPlayerDataBool", "SetPlayerDataInt", "SetPlayerDataFloat", "SetPlayerDataString", "SetPlayerDataVariable",
        "IncrementPlayerDataInt", "DecrementPlayerDataInt", "PlayerDataIntAdd", "SetPersistentBool",
        "SetPersistentBoolSaveData", "SetPersistentInt",
        // Money and what is picked up
        "SetGeoDrop", "SetShardDrop", "CollectableItemCollect", "AddCurrency", "TakeCurrency",
        // The death of a creature
        "SendHealthManagerDeathEvent"
    ];

    /// <summary>
    /// The kinds of action, by name, that the part of an FSM which each game runs by itself does here like the rest of
    /// it, although a copy that runs here otherwise leaves them to the scene host (see
    /// <see cref="IsLeftToSceneHost"/>): what it writes into the save and tells the room. Here they are about the
    /// player whose part it is - the kill that is recorded, the skill that is given, the player kept from harm and
    /// from the pause menu while it plays, the prompt to bind - and the scene host's game does the same for its own
    /// player.
    /// </summary>
    private static readonly HashSet<string> EachGamePartActionNames = [
        "SendEventToRegister", "SendEventToRegisterV2", "SendEventToRegisterDelay",
        "SetPlayerDataBool", "SetPlayerDataInt", "SetPlayerDataFloat", "SetPlayerDataString", "SetPlayerDataVariable",
        "IncrementPlayerDataInt", "DecrementPlayerDataInt", "PlayerDataIntAdd"
    ];

    /// <summary>
    /// Raised when an FSM of an entity goes into the state from which each game runs it by itself (see
    /// <see cref="EntityRegistryEntry.EachGameFrom"/>), in the game that runs the entity, and in the other as the scene
    /// host says so: the boss has fallen, and what comes after it is for both players.
    /// </summary>
    public static event System.Action? EachGamePartBegan;

    /// <summary>
    /// The FSM of the copy that runs here, or null while none does.
    /// </summary>
    private PlayMakerFSM? _runHere;

    /// <summary>
    /// The index of <see cref="_runHere"/> among the FSMs of the entity.
    /// </summary>
    private byte _runHereIndex;

    /// <summary>
    /// The state that the last input played here took <see cref="_runHere"/> to.
    /// </summary>
    private string? _runHereReached;

    /// <summary>
    /// The states that <see cref="_runHere"/> goes through by itself after a catch of the local player: the state the
    /// catch took it to, and the states that only that state leads to, which are the rest of the combo the catch starts
    /// (see <see cref="CatchEvents"/>). The scene host's game plays them too, leaving out what they do to its own
    /// player (see <see cref="PlayForPartner"/>), and what it sends of them is not played again here. Null for any
    /// other input.
    /// </summary>
    private HashSet<FsmState>? _runHereCombo;

    /// <summary>
    /// Whether <see cref="_runHere"/> runs for a catch that one of the copy's parts felt for itself, which this game
    /// leads: every state it goes to is said to the scene host, whose FSM goes there too (see
    /// <see cref="TellCatchWentOn"/>). <see cref="_runHereCombo"/> is then every state that the catch alone leads to.
    /// </summary>
    private bool _runHereLed;

    /// <summary>
    /// The number that the input of the catch this game leads went under (see <see cref="_runHereLed"/>).
    /// </summary>
    private byte _runHereCatch;

    /// <summary>
    /// How many steps of the catch this game leads have been said to the scene host.
    /// </summary>
    private byte _runHereStep;

    /// <summary>
    /// The state out of the catch this game leads that the scene host has been told of, which <see cref="_runHere"/>
    /// waits for the scene host to go to first, or null.
    /// </summary>
    private FsmState? _runHereExit;

    /// <summary>
    /// The catches of the partner's that FSMs of the room's own object play as the partner's game leads them.
    /// </summary>
    private readonly List<Lead> _leads = [];

    /// <summary>
    /// Whether the FSM of the copy that runs here does so for a catch that this game leads.
    /// </summary>
    public bool LeadsACatch => _runHere != null && _runHereLed;

    /// <summary>
    /// Whether the copy of any entity but the given one plays a catch that this game leads, which may hold the local
    /// player now: nothing else lets them go then (see <see cref="EntityFsmActions.LetGoOfTheHeldLocalPlayer"/>).
    /// </summary>
    /// <param name="but">The entity left out, or null.</param>
    public static bool AnyLeadsACatch(Entity? but = null) {
        foreach (var entity in EntitiesByCopy.Values) {
            if (entity != but && entity.LeadsACatch) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <see cref="_runHere"/> runs the part of its FSM that each game runs by itself (see
    /// <see cref="RunEachGamePart"/>), which it does for as long as the entity is in the room: nothing the scene host
    /// says of that FSM takes it back.
    /// </summary>
    private bool _runHereForGood;

    /// <summary>
    /// The FSM of the copy, the state it is to run by itself from and every state that one leads to, heard of from the
    /// scene host while the local player could not be taken along yet, or null (see <see cref="RunEachGamePart"/>).
    /// </summary>
    private (PlayMakerFSM Fsm, FsmState From, HashSet<FsmState> Part)? _eachGamePartWaiting;

    /// <summary>
    /// The number of the last input played here that the scene host has not answered yet, or zero once it has.
    /// </summary>
    private byte _runHereAwaited;

    /// <summary>
    /// When waiting for the scene host to answer <see cref="_runHereAwaited"/> is given up on.
    /// </summary>
    private float _runHereExpiry;

    /// <summary>
    /// Whether the copy's FSM runs here for an input that the scene host has not answered yet. What it sends meanwhile
    /// of that FSM, of how the entity moves by itself and of what it plays is from before the input, and is held.
    /// </summary>
    private bool WaitsForEcho => _runHere != null && _runHereAwaited != 0;

    /// <summary>
    /// What the scene host sent of the FSM that runs here and of how the entity moves by itself while the copy waited
    /// for its echo, in the order it came, copied out of the packets it came in.
    /// </summary>
    private readonly List<EntityNetworkData> _heldData = [];

    /// <summary>
    /// The last animation that the scene host sent while the copy waited for its echo, or null for none.
    /// </summary>
    private (byte Id, tk2dSpriteAnimationClip.WrapMode WrapMode)? _heldAnimation;

    /// <summary>
    /// The actions of <see cref="_runHere"/> that are left to the scene host, switched off while the copy runs here.
    /// </summary>
    private readonly HashSet<FsmStateAction> _mutedHere = [];

    /// <summary>
    /// The FSMs of the room's own object that play on something of the partner's, each with the states it goes through
    /// for it and its actions that are this game's player's own, switched off until it has left those states (see
    /// <see cref="PlayForPartner"/>).
    /// </summary>
    private readonly List<(HutongGames.PlayMaker.Fsm Fsm, HashSet<FsmState> States, List<FsmStateAction> Muted)>
        _playedForPartner = [];

    /// <summary>
    /// Plays at once what the local player's strike or touch makes the FSM at the given index of the copy do, for an
    /// entity whose registry entry says so, or what one of the copy's parts catching the local player makes it do: the
    /// copy's own FSM takes the event in the state the scene host says it is in - or where it is, if it runs here
    /// already - and goes through what the event leads to at once.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="eventName">The event that the strike, touch or catch told the FSM.</param>
    /// <param name="caught">For a catch, what the part that caught set on the FSM along with the event, which the
    /// scene host sets on its FSM too; null for a strike or a touch. A catch other than one of the
    /// <see cref="CatchEvents"/> is one that a part felt for itself, which this game leads (see
    /// <see cref="_runHereLed"/>).</param>
    /// <returns>What the scene host is sent to play the same, or null if it was not played here.</returns>
    public InputStart? PlayHere(byte fsmIndex, string eventName, ToldValues? caught = null) {
        if (CatchEvents.Contains(eventName)) {
            caught ??= ToldValues.None;
        }

        if (!_isControlled || !EntityRegistry.IsLocalFirst(Type) && caught == null ||
            SwitchToStateField == null ||
            fsmIndex >= _fsms.Client.Count || _fsms.Client[fsmIndex] is not { } copyFsm || copyFsm == null ||
            Object.Client == null || _runHere != null && _runHere != copyFsm) {
            return null;
        }

        var fsm = copyFsm.Fsm;
        var fromName = _runHere != null ? fsm.ActiveStateName : EntityFsmActions.HostStateOf(fsm);
        if (fromName == null || fsm.GetState(fromName) is not { } from ||
            EntityFsmActions.FindTransition(fsm, from, eventName) == null) {
            return null;
        }

        if (_runHere == null) {
            StartRunningHere(copyFsm, from);
        }

        // Taken before the input, which the scene host plays from the same start
        Vector2 position = Object.Client.transform.position;
        InputStart.BodyMotion? motion = null;
        if (MovesByItself() && Object.Client.TryGetComponent<Rigidbody2D>(out var body)) {
            motion = new InputStart.BodyMotion(body.linearVelocity, body.rotation, body.angularVelocity);
        }

        // The way it goes, which the scene host follows for a catch that this game leads
        var led = caught != null && !CatchEvents.Contains(eventName);
        var path = new List<FsmState>();

        HeldFsms.Remove(fsm);
        int[] dice;
        try {
            _recordedFsm = fsm;
            _recordedPath = path;
            dice = SharedDice.Record(() => {
                fsm.Event(eventName);
                Settle(fsm);
            }, fsm);
        } finally {
            _recordedFsm = null;
            _recordedPath = null;
            HeldFsms[fsm] = this;
        }

        _runHereIndex = fsmIndex;
        _runHereReached = fsm.ActiveStateName;
        _runHereLed = led && path.Count > 0;
        _runHereCombo = _runHereLed
            ? EntityFsmActions.StatesOnlyThrough(fsm, path[0])
            : caught != null
                ? RestOfTheInput(fsm)
                : null;
        _runHereStep = 0;
        _runHereExit = null;
        _runHereAwaited = BeginAnticipation();
        _runHereCatch = _runHereAwaited;
        _runHereExpiry = Time.unscaledTime + AnticipationHoldTime;
        return new InputStart(
            _runHereReached, position, motion, dice, _runHereAwaited, caught,
            _runHereLed ? path.ConvertAll(state => state.Name).ToArray() : null
        );
    }

    /// <summary>
    /// Tells the scene host that the FSM that runs here for a catch this game leads went on to one of its states
    /// (see <see cref="_runHereLed"/>).
    /// </summary>
    /// <param name="state">The state.</param>
    private void TellCatchWentOn(FsmState state) {
        if (_runHereLed) {
            CatchWentOn?.Invoke(this, _runHereIndex, _runHereCatch, ++_runHereStep, state.Name);
        }
    }

    /// <summary>
    /// Tells the scene host, once, the way out of a catch this game leads that the FSM that runs here would now take
    /// from the state of the catch it is in. It is kept where it is (see <see cref="OnSwitchState"/>) until the scene
    /// host's FSM has gone that way, and follows it again from there.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="toState">The state out of the catch.</param>
    private void TellCatchLeft(HutongGames.PlayMaker.Fsm fsm, FsmState toState) {
        if (!_runHereLed || _runHereExit == toState || fsm.ActiveState is not { } state ||
            _runHereCombo?.Contains(state) != true ||
            !Array.Exists(state.Transitions, transition => transition.ToFsmState == toState)) {
            return;
        }

        _runHereExit = toState;
        CatchWentOn?.Invoke(this, _runHereIndex, _runHereCatch, ++_runHereStep, toState.Name);
    }

    /// <summary>
    /// Takes something the partner did to their copy of the entity that their game has already played on it (see
    /// <see cref="PlayHere"/>), on the scene host. If the event leads anywhere from where the FSM is, the entity is put
    /// where the copy was and set moving as it moved, and the event is played with the dice that the partner's game
    /// rolled, which does here all that it did there and all that only this game does, like telling the room. If that
    /// takes the FSM where it took the copy's, what it set going is carried on for the time the input took to arrive,
    /// so that what this game sends shows the entity where the partner already sees it. Either way the partner hears
    /// back at once (see <see cref="SendEcho"/>).
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="eventName">The event that the partner's strike or touch told the FSM.</param>
    /// <param name="start">What the partner's game sent with it.</param>
    /// <param name="elapsed">How long ago the partner's copy played it, in seconds.</param>
    /// <returns>Whether the entity went the way the partner's copy went.</returns>
    public bool TakeInput(byte fsmIndex, string eventName, InputStart start, float elapsed) {
        if (_isControlled || fsmIndex >= _fsms.Host.Count || _fsms.Host[fsmIndex] is not { } hostFsm ||
            hostFsm == null || Object.Host == null) {
            return false;
        }

        NoteAnticipation(start.Anticipation);

        var fsm = hostFsm.Fsm;
        if (fsm.ActiveState is not { } state || EntityFsmActions.FindTransition(fsm, state, eventName) == null) {
            SendEcho(start.Anticipation, fsmIndex, NotTaken);
            return false;
        }

        Rigidbody2D? body = null;
        if (start.Motion is { } motion && Object.Host.TryGetComponent<Rigidbody2D>(out var hostBody)) {
            body = hostBody;
            PlaceBody(hostBody, start.Position, motion.Angle);
            hostBody.linearVelocity = motion.Velocity;
            hostBody.angularVelocity = motion.Spin;
        } else {
            var transform = Object.Host.transform;
            transform.position = new Vector3(start.Position.x, start.Position.y, transform.position.z);
        }

        // What the part that caught set on the copy's FSM along with the catch, which the part here never did
        var isCatch = start.Caught != null || CatchEvents.Contains(eventName);
        start.Caught?.ApplyTo(fsm);

        // Whatever the partner's game led on this FSM before is over: this comes after all of it
        EndLeadOf(fsm);

        // A catch that the partner's game leads goes the way it went there, and on as it says (TakeCatchState)
        Lead? lead = null;
        var path = start.Path;
        if (path is { Length: > 0 } && EntityFsmActions.FindTransition(fsm, state, eventName) is { } into) {
            lead = new Lead(this, hostFsm, fsmIndex, start.Anticipation, EntityFsmActions.StatesOnlyThrough(fsm, into),
                state) {
                Allowed = into
            };
            BeginLead(lead);
        }

        PlayForPartner(hostFsm, isCatch, () => SharedDice.Throw(start.Dice, () => {
            fsm.Event(eventName);
            if (lead == null) {
                Settle(fsm);
                return;
            }

            for (var i = 1; i < path!.Length && _leads.Contains(lead); i++) {
                FollowCatch(lead, path[i]);
            }
        }, fsm), lead?.States);

        var reached = fsm.ActiveState;
        var sameWay = reached != null && reached.Name == start.State;

        // Gone another way, which the partner's copy follows from here: it lets go of nobody by itself
        if (lead != null && !sameWay && _leads.Contains(lead)) {
            EndLead(lead);
            LetGo(lead, "went another way than the partner's copy");
            reached = fsm.ActiveState;
        }

        if (sameWay) {
            CarryEasesOn(reached!, elapsed);
            if (body != null) {
                CarryOn(body, elapsed);
            }
        }

        // How it moves now is sent whatever the input did to it: nothing else may be left to send
        if (_components.TryGetValue(EntityComponentType.OwnMotion, out var ownMotion)) {
            ((OwnMotionComponent) ownMotion).MarkChanged();
        }

        // The state it went to goes before the echo, which the partner's game holds up against the last state it heard
        // of: a state that runs nothing that is sent is otherwise only sent at the next look at the FSMs
        SendStateChange(fsmIndex);
        SendEcho(start.Anticipation, fsmIndex, reached == null ? NotTaken : (byte) Array.IndexOf(fsm.States, reached));
        return sameWay;
    }

    /// <summary>
    /// Answers an input of the partner that the scene host has just taken: the number it went under, and the FSM and
    /// the state it took that FSM to. It goes with the replays and the rest of what this game sends of the entity, in
    /// order, so the partner's game can tell what was sent before the input was taken from what was sent after.
    /// </summary>
    /// <param name="anticipation">The number the input went under.</param>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="stateIndex">The index of the state it went to, or <see cref="NotTaken"/>.</param>
    private void SendEcho(byte anticipation, byte fsmIndex, byte stateIndex) {
        var data = new EntityNetworkData {
            Type = EntityComponentType.Echo
        };
        data.Packet.Write(anticipation);
        data.Packet.Write(fsmIndex);
        data.Packet.Write(stateIndex);

        _netClient.UpdateManager.AddEntityData(Id, data);
    }

    /// <summary>
    /// Takes the scene host's answer to an input played here (see <see cref="SendEcho"/>). If it is the answer to the
    /// last one and its FSM went where the copy's went, what was held is dropped - the copy has done all of it - and
    /// the copy runs on in step with the scene host until it says more of that FSM. If not, the copy follows the scene
    /// host again at once, with all that was held.
    /// </summary>
    /// <param name="data">The echo.</param>
    private void HearEcho(EntityNetworkData data) {
        var anticipation = data.Packet.ReadByte();
        var fsmIndex = data.Packet.ReadByte();
        var stateIndex = data.Packet.ReadByte();

        // An answer to an input before the last one says nothing of the last one, which is still on its way. The
        // numbers are compared by the sign of their difference, so that going round from the largest to one reads as
        // one forward.
        if (!WaitsForEcho || (sbyte) (anticipation - _runHereAwaited) < 0) {
            return;
        }

        var fsm = _runHere!.Fsm;
        var states = fsm.States;
        if (fsmIndex != _runHereIndex || stateIndex >= states.Length || states[stateIndex].Name != _runHereReached) {
            Logger.Info(
                $"The scene host did not take the input on entity {Id} to '{_runHereReached}', so its copy follows " +
                "the scene host again"
            );
            FollowSceneHost();
            return;
        }

        Logger.Info($"The scene host took the input on entity {Id} to '{_runHereReached}' too");
        var heldAnimation = _heldAnimation;
        _runHereAwaited = 0;
        _heldData.Clear();
        _heldAnimation = null;

        // The scene host's FSM may have gone on already, which it said before this came, and the copy then plays what
        // the scene host last played - unless it has only gone on through the combo of a catch
        var hostState = EntityFsmActions.HostStateOf(fsm);
        if (hostState != _runHereReached && !IsInCombo(hostState)) {
            StopRunningHere();
            if (heldAnimation is { } animation) {
                UpdateAnimation(animation.Id, animation.WrapMode, false);
            }
        }
    }

    /// <summary>
    /// Runs the FSM of the copy that runs here on by a frame, unless waiting for the scene host to answer its input
    /// has gone on longer than any answer takes, which it only does when the scene host never heard of it.
    /// </summary>
    private void UpdateRunHere() {
        RunEachGamePart();

        if (WaitsForEcho && Time.unscaledTime > _runHereExpiry) {
            Logger.Info($"The scene host never answered an input on entity {Id}, so its copy follows it again");
            FollowSceneHost();
        }

        if (_runHere != null && _runHere.gameObject.activeInHierarchy) {
            _runHere.Fsm.Update();
        }
    }

    /// <summary>
    /// Stops the copy's FSM running here and gives the copy all that the scene host sent while it waited, in order, and
    /// what the scene host's FSM holds now.
    /// </summary>
    private void FollowSceneHost() {
        var heldData = new List<EntityNetworkData>(_heldData);
        var heldAnimation = _heldAnimation;
        var fsmIndex = _runHereIndex;
        StopRunningHere();

        TakeVariablesOfSceneHost(fsmIndex);
        UpdateData(heldData, false);
        if (heldAnimation is { } animation) {
            UpdateAnimation(animation.Id, animation.WrapMode, false);
        }
    }

    /// <summary>
    /// Gives the FSM of the copy at the given index what the scene host last said its own FSM holds, which the room's
    /// own object of the entity keeps (see <see cref="UpdateHostFsmData"/>): what was kept off the copy while it ran
    /// here waiting for its echo.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    private void TakeVariablesOfSceneHost(int fsmIndex) {
        var from = _fsms.Host[fsmIndex].FsmVariables;
        var to = _fsms.Client[fsmIndex].FsmVariables;
        for (var i = 0; i < from.FloatVariables.Length && i < to.FloatVariables.Length; i++) {
            to.FloatVariables[i].Value = from.FloatVariables[i].Value;
        }

        for (var i = 0; i < from.IntVariables.Length && i < to.IntVariables.Length; i++) {
            to.IntVariables[i].Value = from.IntVariables[i].Value;
        }

        for (var i = 0; i < from.BoolVariables.Length && i < to.BoolVariables.Length; i++) {
            to.BoolVariables[i].Value = from.BoolVariables[i].Value;
        }

        for (var i = 0; i < from.StringVariables.Length && i < to.StringVariables.Length; i++) {
            to.StringVariables[i].Value = from.StringVariables[i].Value;
        }

        for (var i = 0; i < from.Vector2Variables.Length && i < to.Vector2Variables.Length; i++) {
            to.Vector2Variables[i].Value = from.Vector2Variables[i].Value;
        }

        for (var i = 0; i < from.Vector3Variables.Length && i < to.Vector3Variables.Length; i++) {
            to.Vector3Variables[i].Value = from.Vector3Variables[i].Value;
        }
    }

    /// <summary>
    /// Holds something that the scene host sent while the copy waited for its echo (see <see cref="HearEcho"/>).
    /// </summary>
    /// <param name="data">What it sent, whose packet is only lent for as long as it is being read.</param>
    private void HoldForEcho(EntityNetworkData data) {
        _heldData.Add(new EntityNetworkData {
            Type = data.Type,
            SenderId = data.SenderId,
            Packet = new global::SSMP.Networking.Packet.Packet(data.Packet.ToArray())
        });
    }

    /// <summary>
    /// Takes it that the scene host's game sent a replay of an action of an FSM of the copy. While the copy runs that
    /// FSM here in step with the scene host, the scene host did something that the copy did not - its own player struck
    /// the flea too - and the copy follows it again from here; but not for a state of the combo of a catch, which the
    /// copy plays by itself (see <see cref="_runHereCombo"/>), nor for the part that each game runs by itself.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="state">The state of the action.</param>
    private void HearFromSceneHost(PlayMakerFSM fsm, FsmState state) {
        if (fsm == _runHere && !WaitsForEcho && !_runHereForGood && _runHereCombo?.Contains(state) != true) {
            CatchUpWithSceneHost();
        }
    }

    /// <summary>
    /// Takes it that the scene host's FSM went to a state. While the copy runs that FSM here in step with the scene
    /// host, the scene host has gone on, unless that is the state the copy is in or one of the combo of a catch, which
    /// the copy goes through by itself: the copy follows it again from here. Any state of the part of the FSM that each
    /// game runs by itself starts that here from its first state (see <see cref="RunEachGamePart"/>), after a player
    /// lying in their cocoon is stood up for it (<see cref="EachGamePartBegan"/>). Any of them, as only the latest
    /// state of the scene host's FSM is sent, and the short one that the part is gone into by can be passed over.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="stateName">The state.</param>
    private void HearStateFromSceneHost(PlayMakerFSM fsm, string stateName) {
        if (!_runHereForGood && _eachGamePartWaiting == null &&
            EntityRegistry.TryGetEachGameFrom(Type, fsm.FsmName, out var fromName)) {
            // Every state that the first one leads to on its own, the binding that goes round as often as the boss
            // takes to be bound among them
            var from = fsm.Fsm.GetState(fromName);
            var part = new HashSet<FsmState> { from };
            var toGo = new Stack<FsmState>();
            toGo.Push(from);
            while (toGo.Count > 0) {
                foreach (var transition in toGo.Pop().Transitions) {
                    if (transition.ToFsmState is { } to && part.Add(to)) {
                        toGo.Push(to);
                    }
                }
            }

            if (part.Contains(fsm.Fsm.GetState(stateName))) {
                _eachGamePartWaiting = (fsm, from, part);
                EachGamePartBegan?.Invoke();
                RunEachGamePart();
                return;
            }
        }

        if (fsm == _runHere && !WaitsForEcho && !_runHereForGood && stateName != fsm.ActiveStateName &&
            !IsInCombo(stateName)) {
            CatchUpWithSceneHost();
        }
    }

    /// <summary>
    /// Runs the FSM of the copy by itself from the state from which each game runs it by itself (see
    /// <see cref="EntityRegistryEntry.EachGameFrom"/>), once the scene host has said its own is in the part that state
    /// leads to and the local player can be taken along. That is the end of a boss, which takes the local player along
    /// as the scene host's player is in the other game: to bind the boss with their own button, get what it gives and
    /// go into its memory, or to hear its last words and go back out of the memory it was fought in. Only the scene
    /// host's player used to: the copy's replays of taking "the player" along are kept off the local one
    /// (EntityFsmActions.ActsOnTheLocalPlayer), and what the boss runs of a template is not sent at all, so the other
    /// player stood by and watched the binding, or was left in the memory.
    ///
    /// It runs on for good: none of what the scene host says of that FSM is taken any more but what the copy leaves to
    /// it (see <see cref="IsLeftToSceneHost"/>), which does not count what the part writes into the save and tells the
    /// room (see <see cref="EachGamePartActionNames"/>). It goes through every state of the part.
    /// </summary>
    private void RunEachGamePart() {
        if (_eachGamePartWaiting is not { } waiting || !EntityFsmActions.IsLocalPlayerFree()) {
            return;
        }

        _eachGamePartWaiting = null;
        var (copyFsm, from, part) = waiting;

        // Whatever ran here for an input of the local player is over
        StopRunningHere();

        _runHereForGood = true;
        StartRunningHere(copyFsm, from);
        _runHereCombo = part;

        // Done now, as the scene host's FSM did it on going in. It is not gone into again: the copy is told of the
        // death that the scene host's game sends as well (HealthManagerComponent), which would start it all over
        copyFsm.Fsm.SetState(from.Name);
        _runHereCombo.Remove(from);

        Logger.Info($"The copy of entity {Id} runs '{copyFsm.FsmName}' by itself from '{from.Name}' for this player");
    }

    /// <summary>
    /// Stops the copy's FSM running here, which follows the scene host again from here, and plays the last animation
    /// that the scene host sent while the copy went through the combo of a catch by itself - unless the copy shows it
    /// already, having played that part of the combo itself: played again, it would start over.
    /// </summary>
    private void CatchUpWithSceneHost() {
        var heldAnimation = _heldAnimation;
        StopRunningHere();
        if (heldAnimation is { } animation && !(_animationClipNameIds.TryGetValue(animation.Id, out var clipName) &&
                                                _animator.Client != null &&
                                                _animator.Client.CurrentClip?.name == clipName)) {
            UpdateAnimation(animation.Id, animation.WrapMode, false);
        }
    }

    /// <summary>
    /// Whether the state of the given name is one of the combo of a catch that the FSM that runs here goes through by
    /// itself (see <see cref="_runHereCombo"/>).
    /// </summary>
    private bool IsInCombo(string? stateName) {
        return stateName != null && _runHereCombo?.Contains(_runHere!.Fsm.GetState(stateName)) == true;
    }

    /// <summary>
    /// Plays on an FSM of the room's own object something that the partner did to their copy of the entity, or that
    /// caught them. A catch is played but for what is this game's own player's (see
    /// <see cref="EntityFsmActions.IsThePlayersOwn"/>). The FSM knows of no player but this game's, so when one of the
    /// creature's parts caught the partner, it held this game's player in place for the slashes, wherever they were.
    /// What the combo does to the partner is theirs, and their own game plays it on them (see <see cref="PlayHere"/>).
    /// Those actions are switched off before it plays, and on again for the states that the catch took it through at
    /// once; for the state it stands in then, and those that only that state leads to, they stay off until it has left
    /// them (see <see cref="LetThePlayerBackIn"/>). A strike or a touch is played as it is: what it sets off can be a
    /// whole round of the creature's attacks, which goes on at this game's player too.
    /// </summary>
    /// <param name="hostFsm">The FSM.</param>
    /// <param name="isCatch">Whether it is a catch rather than a strike or a touch.</param>
    /// <param name="play">What plays it on the FSM.</param>
    /// <param name="catchStates">The states of a catch that the partner's game leads, for which the actions stay off
    /// however the FSM goes through them; null for the state it stands in once played and those it alone leads to.
    /// </param>
    public void PlayForPartner(
        PlayMakerFSM hostFsm,
        bool isCatch,
        System.Action play,
        HashSet<FsmState>? catchStates = null
    ) {
        if (!isCatch) {
            play();
            return;
        }

        var fsm = hostFsm.Fsm;
        var muted = EntityFsmActions.PlayersOwnActionsOf(fsm);
        foreach (var action in muted) {
            action.Enabled = false;
        }

        // Whatever the game's code does on the way, what is switched off is never left off for good
        try {
            play();
        } finally {
            KeepOffForWhatIsLeft(fsm, muted, catchStates ?? RestOfTheInput(fsm));
        }
    }

    /// <summary>
    /// Switches back on the actions kept off this game's player while an FSM played something of the partner's, but
    /// for those of the given states, which are the rest of what it played; those stay off until it has left them
    /// (see <see cref="LetThePlayerBackIn"/>).
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="muted">The actions that were switched off.</param>
    /// <param name="states">The states.</param>
    private void KeepOffForWhatIsLeft(
        HutongGames.PlayMaker.Fsm fsm,
        List<FsmStateAction> muted,
        HashSet<FsmState> states
    ) {
        muted.RemoveAll(action => {
            if (states.Contains(action.State)) {
                return false;
            }

            action.Enabled = true;
            return true;
        });

        if (muted.Count == 0) {
            return;
        }

        // Played again before it left what it played before, it keeps all of it off until it has left all of it
        var index = _playedForPartner.FindIndex(played => played.Fsm == fsm);
        if (index < 0) {
            _playedForPartner.Add((fsm, states, muted));
        } else {
            _playedForPartner[index].States.UnionWith(states);
            _playedForPartner[index].Muted.AddRange(muted);
        }
    }

    /// <summary>
    /// The rest of what an input does to an FSM once it has played: the state it took the FSM to, and the states that
    /// only that state leads to - for a catch, the rest of its combo.
    /// </summary>
    private static HashSet<FsmState> RestOfTheInput(HutongGames.PlayMaker.Fsm fsm) {
        var states = new HashSet<FsmState>();
        if (fsm.ActiveState is { } reached) {
            states.Add(reached);
            EntityFsmActions.AddStatesEnteredOnlyFrom(fsm, states);
        }

        return states;
    }

    /// <summary>
    /// Switches back on what is this game's player's own in each FSM that played something of the partner's (see
    /// <see cref="PlayForPartner"/>) and has left the states it went through for it, or in all of them.
    /// </summary>
    /// <param name="all">Whether to switch it all back on, wherever the FSMs are.</param>
    private void LetThePlayerBackIn(bool all) {
        for (var i = _playedForPartner.Count - 1; i >= 0; i--) {
            var (fsm, states, muted) = _playedForPartner[i];
            if (!all && fsm.ActiveState is { } state && states.Contains(state)) {
                continue;
            }

            foreach (var action in muted) {
                action.Enabled = true;
            }

            _playedForPartner.RemoveAt(i);
        }
    }

    /// <summary>
    /// Switches back on at once what is this game's player's own in an FSM that has just left a catch of the partner's
    /// which their game led, the way it left it being no global transition: the states of the catch are all that was
    /// kept off, and it may go straight back into them for a catch of this game's own player, before the look once a
    /// frame (see <see cref="LetThePlayerBackIn"/>) would see that it left them.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private void LetThePlayerBackInTo(HutongGames.PlayMaker.Fsm fsm) {
        var index = _playedForPartner.FindIndex(played => played.Fsm == fsm);
        if (index < 0) {
            return;
        }

        foreach (var action in _playedForPartner[index].Muted) {
            action.Enabled = true;
        }

        _playedForPartner.RemoveAt(index);
    }

    /// <summary>
    /// Starts the copy's FSM running here, in the state it takes the input in.
    /// </summary>
    /// <param name="copyFsm">The FSM of the copy.</param>
    /// <param name="from">The state it takes the input in.</param>
    private void StartRunningHere(PlayMakerFSM copyFsm, FsmState from) {
        var fsm = copyFsm.Fsm;

        // What the copy replayed of the scene host's state is left: its own FSM moves it from here
        EntityFsmActions.LeaveStatesOf([copyFsm]);
        CreateOwnObjects(fsm);

        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                if (action is { Enabled: true } && IsLeftToSceneHost(action) &&
                    !(_runHereForGood && EachGamePartActionNames.Contains(action.GetType().Name))) {
                    action.Enabled = false;
                    _mutedHere.Add(action);
                }
            }
        }

        // Put in the state it takes the input in without doing that state again: done again, a flea falling from the
        // top of its flight was put back at the top. A state with nothing to do goes on at once, so for the moment of
        // going in it has only something that does nothing.
        var actions = from.Actions;
        from.Actions = [new HoldHere()];
        try {
            fsm.SetState(from.Name);
            fsm.Start();
        } finally {
            from.Actions = actions;
        }

        _runHere = copyFsm;
        HookSwitchState();
        HeldFsms[fsm] = this;
    }

    /// <summary>
    /// Puts <see cref="_switchStateHook"/> in place, if it is not yet.
    /// </summary>
    private static void HookSwitchState() {
        _switchStateHook ??= new Hook(
            typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                "SwitchState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(FsmState)], null
            )!,
            new Action<Action<HutongGames.PlayMaker.Fsm, FsmState>, HutongGames.PlayMaker.Fsm, FsmState>(OnSwitchState)
        );
    }

    /// <summary>
    /// Stops the copy's FSM running here, after which the copy follows the scene host again from where it is. A catch
    /// that this game led is over with it: a player that the creature still holds is let go, since what lets them go
    /// in the end was the copy's own FSM to do, and the scene host's FSM does not do it to them.
    /// </summary>
    /// <param name="letGoOfThePlayer">Whether a player held by a catch that this game led is let go, which they are
    /// not when the room's own creature takes over and goes on holding them.</param>
    private void StopRunningHere(bool letGoOfThePlayer = true) {
        if (_runHere is not { } copyFsm) {
            return;
        }

        var led = _runHereLed;
        _runHere = null;
        _runHereCombo = null;
        _runHereLed = false;
        _runHereForGood = false;
        _runHereAwaited = 0;
        _heldData.Clear();
        _heldAnimation = null;
        foreach (var action in _mutedHere) {
            action.Enabled = true;
        }

        _mutedHere.Clear();

        // Let go only once stopped: stopping tells the FSM that it is switched off, which some take somewhere
        var fsm = copyFsm.Fsm;
        if (copyFsm != null) {
            fsm.Stop();
        }

        HeldFsms.Remove(fsm);

        if (led && letGoOfThePlayer && !AnyLeadsACatch(this)) {
            EntityFsmActions.LetGoOfTheHeldLocalPlayer($"the copy of entity {Id} no longer plays the catch");
        }
    }

    /// <summary>
    /// Lets go of the FSM of the copy that runs here without stopping it, for an entity that goes away with its room:
    /// stopping runs the game's own code for leaving a state, which nothing needs of a copy that is going.
    /// </summary>
    private void LetGoOfRunHere() {
        if (_runHere is not { } copyFsm) {
            return;
        }

        HeldFsms.Remove(copyFsm.Fsm);
        _runHere = null;
        _runHereLed = false;
    }

    /// <summary>
    /// Hook for an FSM going to another state, which keeps the FSM of a copy that runs here where it is unless an input
    /// of the local player takes it on, or it goes on through the combo of a catch (see <see cref="HeldFsms"/>), and
    /// the FSM of the room's own object that plays a catch that the partner's game leads on the way that game says (see
    /// <see cref="LetsLedFsmGo"/>). The state it was about to go to is cleared, as the game clears it once it has gone
    /// there, so that the game does not try again. For a catch this game leads, every state the copy goes to is said
    /// to the scene host, and so is the way out it waits at (see <see cref="TellCatchWentOn"/>).
    /// </summary>
    private static void OnSwitchState(
        Action<HutongGames.PlayMaker.Fsm, FsmState> orig,
        HutongGames.PlayMaker.Fsm self,
        FsmState toState
    ) {
        if (self == _recordedFsm) {
            _recordedPath?.Add(toState);
            orig(self, toState);
            return;
        }

        if (HeldFsms.Count > 0 && HeldFsms.TryGetValue(self, out var entity)) {
            if (entity._runHereCombo?.Contains(toState) == true) {
                // Said before it goes there, since going there may take it on further at once
                entity.TellCatchWentOn(toState);
                orig(self, toState);
                return;
            }

            SwitchToStateField!.SetValue(self, null);
            entity.TellCatchLeft(self, toState);
            return;
        }

        if (LedFsms.Count > 0 && LedFsms.TryGetValue(self, out var lead) && !lead.Owner.LetsLedFsmGo(lead, toState)) {
            SwitchToStateField!.SetValue(self, null);
            return;
        }

        orig(self, toState);
    }

    /// <summary>
    /// Takes a state that the partner's copy of the entity went to in a catch that their game leads, on the scene
    /// host (see <see cref="CatchWentOn"/>): the FSM of the room's own object that plays the catch goes there too,
    /// unless it has already left the catch its own way. What it does there is this game's but for what is this
    /// game's player's own (see <see cref="PlayForPartner"/>).
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="catchNumber">The number that the catch went under.</param>
    /// <param name="step">The number of the step, counting from one after the input.</param>
    /// <param name="stateName">The state.</param>
    public void TakeCatchState(byte fsmIndex, byte catchNumber, byte step, string stateName) {
        var lead = _leads.Find(led => led.FsmIndex == fsmIndex);

        // Steps are compared by the sign of their difference, so that going round from the largest to one reads as
        // one forward; an older one, come late, says nothing any more
        if (_isControlled || lead == null || lead.Number != catchNumber || (sbyte) (step - lead.Step) <= 0) {
            return;
        }

        lead.Step = step;
        lead.LastHeard = Time.unscaledTime;
        FollowCatch(lead, stateName);
    }

    /// <summary>
    /// Takes the FSM that plays a catch the partner's game leads to a state of it that the partner's copy went to, or
    /// out of it the way the partner's copy would go, which ends the lead.
    /// </summary>
    /// <param name="lead">The catch.</param>
    /// <param name="stateName">The state.</param>
    private void FollowCatch(Lead lead, string stateName) {
        var fsm = lead.Fsm;
        if (fsm.GetState(stateName) is not { } state) {
            return;
        }

        lead.Allowed = state;
        try {
            fsm.SetState(stateName);
        } finally {
            lead.Allowed = null;
        }

        if (!lead.States.Contains(state)) {
            EndLead(lead);
            LetThePlayerBackInTo(fsm);
        }
    }

    /// <summary>
    /// Whether an FSM of the room's own object that plays a catch of the partner's which their game leads may go to a
    /// state by itself. It goes where the partner's game says (see <see cref="TakeCatchState"/>), and by itself only
    /// out of the catch: its death, a stun, the catch ending by its own clock. Nothing of this game's player moves it:
    /// their being hurt, which tells the whole room, nor anything else the catch waits for, like a struggle. Going out
    /// of the catch by a global transition - killed while it held the partner - it lets go of the partner there, and
    /// what it does to "the player" on the way is kept off this game's player (see
    /// <see cref="KeepOffForTheAftermath"/>).
    /// </summary>
    /// <param name="lead">The catch.</param>
    /// <param name="toState">The state.</param>
    private bool LetsLedFsmGo(Lead lead, FsmState? toState) {
        if (toState == null) {
            return true;
        }

        if (lead.Allowed == toState) {
            lead.Allowed = null;
            return true;
        }

        var fsm = lead.Fsm;
        var transition = fsm.LastTransition;
        if (lead.States.Contains(toState) ||
            transition != null && EntityFsmActions.IsPlayerEvent(transition.EventName)) {
            return false;
        }

        EndLead(lead);
        if (transition != null && Array.IndexOf(fsm.GlobalTransitions, transition) >= 0) {
            KeepOffForTheAftermath(lead, toState);
        } else {
            LetThePlayerBackInTo(fsm);
        }

        return true;
    }

    /// <summary>
    /// Keeps what is this game's player's own off the states that an FSM playing a catch of the partner's goes into by
    /// a global transition, and those only that state leads to, until it has left them (see
    /// <see cref="LetThePlayerBackIn"/>): a creature killed while it holds a player lets go of them, with a last
    /// blow, and here that player is the partner. Not if that takes it back to where it was before the catch, which is
    /// its own life again.
    /// </summary>
    /// <param name="lead">The catch.</param>
    /// <param name="to">The state that the global transition leads to.</param>
    private void KeepOffForTheAftermath(Lead lead, FsmState to) {
        var fsm = lead.Fsm;
        var after = EntityFsmActions.StatesOnlyThrough(fsm, to);
        if (after.Contains(lead.From)) {
            return;
        }

        var muted = EntityFsmActions.PlayersOwnActionsOf(fsm).FindAll(action => after.Contains(action.State));
        foreach (var action in muted) {
            action.Enabled = false;
        }

        var index = _playedForPartner.FindIndex(played => played.Fsm == fsm);
        if (index < 0) {
            _playedForPartner.Add((fsm, after, muted));
        } else {
            _playedForPartner[index].States.UnionWith(after);
            _playedForPartner[index].Muted.AddRange(muted);
        }
    }

    /// <summary>
    /// Starts an FSM of the room's own object following a catch that the partner's game leads.
    /// </summary>
    private void BeginLead(Lead lead) {
        HookSwitchState();
        _leads.Add(lead);
        LedFsms[lead.Fsm] = lead;
    }

    /// <summary>
    /// Stops an FSM following a catch that the partner's game led. It goes its own way from here.
    /// </summary>
    private void EndLead(Lead lead) {
        if (!_leads.Remove(lead)) {
            return;
        }

        if (LedFsms.TryGetValue(lead.Fsm, out var led) && led == lead) {
            LedFsms.Remove(lead.Fsm);
        }
    }

    /// <summary>
    /// Stops the given FSM following a catch that the partner's game led, if it does.
    /// </summary>
    private void EndLeadOf(HutongGames.PlayMaker.Fsm fsm) {
        if (_leads.Find(lead => lead.Fsm == fsm) is { } lead) {
            EndLead(lead);
        }
    }

    /// <summary>
    /// Stops every FSM of the entity following a catch that the partner's game led.
    /// </summary>
    private void EndAllLeads() {
        for (var i = _leads.Count - 1; i >= 0; i--) {
            EndLead(_leads[i]);
        }
    }

    /// <summary>
    /// Looks at the catches of the partner's that FSMs of the room's own object follow, on the scene host, once a
    /// frame. One whose FSM has left it by a way that the hook did not see is over. One that the partner's game has
    /// said nothing more of for <see cref="LeadTimeout"/> lets go by itself (see <see cref="LetGo"/>): the player it
    /// held left, and the FSM would otherwise wait for their struggles for good.
    /// </summary>
    private void UpdateLeads() {
        for (var i = _leads.Count - 1; i >= 0; i--) {
            var lead = _leads[i];
            if (lead.Fsm.ActiveState is not { } state || !lead.States.Contains(state)) {
                EndLead(lead);
                continue;
            }

            if (Time.unscaledTime - lead.LastHeard >= LeadTimeout) {
                EndLead(lead);
                LetGo(lead, $"heard nothing more of the catch for {LeadTimeout} s");
            }
        }
    }

    /// <summary>
    /// Takes an FSM that followed a catch the partner's game led, and follows it no more, out of the catch the
    /// shortest way there is from the state of it that it stands in, if it stands in one: the creature lets go of a
    /// player it has nobody to hold for.
    /// </summary>
    /// <param name="lead">The catch, over.</param>
    /// <param name="why">Why, for the log.</param>
    private void LetGo(Lead lead, string why) {
        var fsm = lead.Fsm;
        if (fsm.ActiveState is not { } from || !lead.States.Contains(from)) {
            return;
        }

        var seen = new HashSet<FsmState> { from };
        var toGo = new Queue<FsmState>();
        toGo.Enqueue(from);
        while (toGo.Count > 0) {
            foreach (var transition in toGo.Dequeue().Transitions) {
                if (transition.ToFsmState is not { } to || !seen.Add(to)) {
                    continue;
                }

                if (!lead.States.Contains(to)) {
                    Logger.Info($"The '{fsm.Name}' of entity {Id} {why}, so it goes from '{from.Name}' to '{to.Name}'");
                    fsm.SetState(to.Name);
                    LetThePlayerBackInTo(fsm);
                    return;
                }

                toGo.Enqueue(to);
            }
        }

        Logger.Info($"The '{fsm.Name}' of entity {Id} {why}, and has no way out of '{from.Name}'");
    }

    /// <summary>
    /// A catch of the partner's that an FSM of the room's own object plays as the partner's game leads it (see
    /// <see cref="TakeCatchState"/>).
    /// </summary>
    /// <param name="owner">The entity.</param>
    /// <param name="hostFsm">The FSM.</param>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="number">The number that the catch went under.</param>
    /// <param name="states">The states of the catch: those that the state it went to alone leads to.</param>
    /// <param name="from">The state the FSM took the catch in.</param>
    private sealed class Lead(
        Entity owner,
        PlayMakerFSM hostFsm,
        byte fsmIndex,
        byte number,
        HashSet<FsmState> states,
        FsmState from
    ) {
        public Entity Owner { get; } = owner;
        public HutongGames.PlayMaker.Fsm Fsm { get; } = hostFsm.Fsm;
        public byte FsmIndex { get; } = fsmIndex;
        public byte Number { get; } = number;
        public HashSet<FsmState> States { get; } = states;
        public FsmState From { get; } = from;

        /// <summary>
        /// The last step that the partner's game said.
        /// </summary>
        public byte Step { get; set; }

        /// <summary>
        /// When the partner's game last said anything of the catch.
        /// </summary>
        public float LastHeard { get; set; } = Time.unscaledTime;

        /// <summary>
        /// The one state that the FSM is let go to next, however it goes there, or null.
        /// </summary>
        public FsmState? Allowed { get; set; }
    }

    /// <summary>
    /// Whether an action of an FSM that runs on a copy does something beyond the entity itself to what both games
    /// share, which is left to the scene host: switched off while the copy runs here, and taken from the scene host's
    /// game even while the rest of what it sends of that FSM is held (see <see cref="HoldForEcho"/>). These are exactly
    /// what only the scene host's game does when the copy does not run: what it tells the room and other things, which
    /// it sends here - a point told here as well would count twice - and the save, money and creatures, which reach
    /// this game their own ways or not at all. A call on something else that the scene host's game does not send, like
    /// the reaction that a harpooned flea sets off on the player who harpooned it, is the player's own and runs here.
    /// So does what it tells or sets on the local player (see <see cref="EntityFsmActions.IsThePlayersOwn"/>), like
    /// the way a creature that caught them turns them to face it: the scene host leaves that out (see
    /// <see cref="PlayForPartner"/>).
    /// </summary>
    private static bool IsLeftToSceneHost(FsmStateAction action) {
        var fsm = action.Fsm;
        var leftToSceneHost = SharedEffectActionNames.Contains(action.GetType().Name) || action switch {
            // Told to anything but the entity itself and its parts, which each game has of its own: the blade that
            // lets go of the player at the end of a combo is told so here, where it caught them
            SendEventByName send => !IsToItsOwn(fsm, send.eventTarget),
            SendEventByNameV2 send => !IsToItsOwn(fsm, send.eventTarget),
            SendMessage send => !IsItsOwn(fsm, fsm.GetOwnerDefaultTarget(send.gameObject)),
            // A creature, which lives where the scene host's game runs it
            CreateObject create => IsEntity(create.gameObject.Value),
            SpawnObjectFromGlobalPool spawn => IsEntity(spawn.gameObject.Value),
            _ => false
        };
        return leftToSceneHost && !EntityFsmActions.IsThePlayersOwn(action);

        static bool IsToItsOwn(HutongGames.PlayMaker.Fsm fsm, FsmEventTarget target) {
            return target.target == FsmEventTarget.EventTarget.Self ||
                   target.target is FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM &&
                   IsItsOwn(fsm, fsm.GetOwnerDefaultTarget(target.gameObject));
        }

        // The copy has none of the parts that are entities of their own (DestroyManagedChildren)
        static bool IsItsOwn(HutongGames.PlayMaker.Fsm fsm, GameObject? target) {
            return target != null && target.transform.IsChildOf(fsm.GameObject.transform);
        }

        static bool IsEntity(GameObject? prefab) {
            return prefab != null && EntityRegistry.TryGetEntry(prefab, out _);
        }
    }

    /// <summary>
    /// Creates what the first state of the copy's FSM creates for the FSM to use, which the copy never had, since its
    /// FSM never started: the empty object that a flea judges where it can fly by. Without it, a flea judged every spot
    /// too far and rolled for ever.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    private static void CreateOwnObjects(HutongGames.PlayMaker.Fsm fsm) {
        if (fsm.GetState(fsm.StartState) is not { } first) {
            return;
        }

        foreach (var action in first.Actions) {
            if (action is CreateEmptyObject { storeObject.Value: null } create) {
                // Marked done already, so that it does not tell a state that is not running that it is
                create.Init(first);
                create.Finished = true;
                create.OnEnter();
            }
        }
    }

    /// <summary>
    /// Steps an FSM on at once for as long as it waits for the next frame, up to <see cref="MaxSettleSteps"/> times. A
    /// flea that rolls a spot too near or too far rolls again at once, but after a number of those it waits for the
    /// next frame first. Stepped on here instead, it rolls within the input, with the dice that go with it, rather than
    /// on a frame of its own, for which each game would roll its own.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private static void Settle(HutongGames.PlayMaker.Fsm fsm) {
        for (var step = 0; step < MaxSettleSteps && NextFrameEnterField != null; step++) {
            if (fsm.ActiveState is not { } state || Array.Find(
                    state.Actions, action => action is NextFrameEvent { Entered: true, Finished: false }
                ) is not { } next) {
                return;
            }

            NextFrameEnterField.SetValue(next, -1);
            fsm.Update();
        }
    }

    /// <summary>
    /// Carries the eased movements of a state on by the given time, as if the state had been entered that long ago.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="elapsed">The time, in seconds.</param>
    private static void CarryEasesOn(FsmState state, float elapsed) {
        if (EaseRunningTimeField == null) {
            return;
        }

        foreach (var action in state.Actions) {
            if (action is EaseFsmAction { Enabled: true, Finished: false } ease) {
                EaseRunningTimeField.SetValue(ease, (float) EaseRunningTimeField.GetValue(ease) + elapsed);
            }
        }
    }

    /// <summary>
    /// An action that does nothing and is never done, which holds an FSM in a state without doing what the state does.
    /// </summary>
    private sealed class HoldHere : FsmStateAction {
    }
}
