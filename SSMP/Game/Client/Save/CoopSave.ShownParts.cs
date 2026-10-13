using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Controllers of a fight whose parts the other game shows the way they go up in the scene host's game, in a checked
/// two-player save. In the fight on the stage, two controllers of the room do what the creature only sets going, and
/// their own clocks set them going again too: one sets off flares over the stage that burst in rings that hurt, picking
/// from its list which go up, how high and after what wait, and each flare as it goes up rolls how far to one side it
/// bursts and how large; the other bursts the floor open in a row of its pick, each burst after a wait of its own. The
/// controllers are no creatures, so each game ran its own, and the rings and the bursts came at other places and times
/// for each player (USER 10-13).
///
/// Only the scene host's game rolls (USER 10-13: 要么就房主投骰子，客机只同步骰子骰了啥). The dice of the flares went
/// into a list that the controller shuffles where it stands and picks from in turn, so the same dice gave each game
/// another flare once the lists stood otherwise (USER 10-13: 骰子先供给给一个列表，还要再客机那边后处理的就不行). The
/// host's controllers go on as the game has them, and the other game is told what came of it, part by part:
/// - a part that went up - a flare, a burst of the floor - with where it stands, and for a part that starts a state
///   machine of its own as it goes up, what it came to as it started: how large it is, where its pieces are and what
///   its flags say, like whether a flare hurts;
/// - a part that moved while it is up, or whose flags changed, like a flare that is told not to hurt as the creature
///   dies;
/// - the sparkle that a controller plays over the stage as the flares are about to go up;
/// - a part that went out, with how long it was up: one that the host's controller put out early, as the floor bursts
///   of a round that a new round cuts short, goes out here after as long, rather than staying up and hurting.
/// The other game's controllers find their parts and do nothing more - each is held from its first step on, as the
/// states of a controller of a fight are - and its parts go up as the host's words come. Nothing is rolled or worked
/// out again there (USER 10-13: 烟花的位置不能直接拿到吗); from there each part runs by itself, the way the host's does:
/// a flare waits, bursts and goes out, a burst plays and goes out. What a part does to a player, it does in that
/// player's game, as before. The way the flash of a flare is turned as it bursts is rolled in each game: it is rolled a
/// second after the flare goes up and would take a word of its own, and the ring that hurts is round (USER 10-13).
///
/// Each word carries the time in the host's game, and the words are taken in that order, the time between two kept as
/// it was there, so words that come together after the network stalled go up one after another as they did there,
/// rather than all at once (USER 10-13: 网络波动一下). A word after a pause longer than any wait within a round goes up
/// as it comes. A word more than a moment older than the newest that came is of something that is over in the host's
/// game, and is left, unless it puts a part out: so a long stall shows the last moment before it ended, not everything
/// late. A part that is still up here from before goes up afresh, as the host's did.
///
/// The parts are the objects under the controller that its variables hold, since those are what its actions switch on
/// and move, and those it plays the sparkle on. A part that was up already when the partner came in is not told, nor
/// one that went up before this game ran the room.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The longest time between two words of the parts of one controller, in seconds, that is kept between them here:
    /// longer than any wait between the parts of one round, shorter than the pause between rounds.
    /// </summary>
    private const float ShownPartSpacingLimit = 1.5f;

    /// <summary>
    /// How much older than the newest word of a controller a word may be, in seconds of the host's game, and still be
    /// taken: about the time a flare waits before it bursts.
    /// </summary>
    private const float ShownPartLateLimit = 1.5f;

    /// <summary>
    /// How many numbers of words of each controller are kept to know a word that comes again.
    /// </summary>
    private const int ShownPartSeenLimit = 256;

    /// <summary>
    /// How far a part that is up has to move, squared, in units, to be told.
    /// </summary>
    private const float ShownPartMoveSqr = 0.0001f;

    /// <summary>
    /// What a word about a part of a controller says happened to it, in <see cref="CoopSaveUpdate.Part"/>.
    /// </summary>
    private enum PartChange : ushort {
        /// <summary>
        /// It moved while up, or its flags changed. The word holds the time in the host's game and where it stands,
        /// and for a part with a state machine its flags.
        /// </summary>
        Moved = 0,

        /// <summary>
        /// It went up. The word also holds, for a part with a state machine, its size and the place and size of each
        /// of its pieces, and its flags.
        /// </summary>
        WentUp = 1,

        /// <summary>
        /// Its particles were played. The word also holds how many were let out at once, or 0 for playing them, and 1
        /// if they start over when they play already.
        /// </summary>
        Played = 2,

        /// <summary>
        /// It went out. The word also holds how long it was up.
        /// </summary>
        WentOut = 3
    }

    /// <summary>
    /// The controllers here whose parts the other game shows, with what is known of their parts.
    /// </summary>
    private readonly Dictionary<Fsm, ShownController> _shownControllers = new();

    /// <summary>
    /// The controllers of <see cref="_shownControllers"/>, gathered to be gone through each frame, since showing a part
    /// can note another controller.
    /// </summary>
    private readonly List<Fsm> _shownControllerList = [];

    /// <summary>
    /// The state machines of a part, gathered to read its flags, kept to be used again.
    /// </summary>
    private static readonly List<PlayMakerFSM> PartMachines = [];

    /// <summary>
    /// The hook on a state machine playing the particles of an object.
    /// </summary>
    private Hook? _particlePlayHook;

    /// <summary>
    /// Registers the hook on a state machine playing particles, which a controller whose parts the other game shows
    /// tells.
    /// </summary>
    private void RegisterShownPartHooks() {
        _particlePlayHook = CreateHook(
            typeof(PlayParticleEmitter).GetMethod("OnEnter", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<PlayParticleEmitter>, PlayParticleEmitter>(OnParticlePlay)
        );
    }

    /// <summary>
    /// Notes a controller whose parts the other game shows, the first time it is seen.
    /// </summary>
    private ShownController NoteShownController(Fsm fsm) {
        if (!_shownControllers.TryGetValue(fsm, out var shown)) {
            shown = new ShownController();
            _shownControllers[fsm] = shown;
        }

        return shown;
    }

    /// <summary>
    /// Whether this game tells the partner what the parts of its controllers do: it runs the room, with a member that
    /// the saves were checked with in it.
    /// </summary>
    private bool TellsShownParts() {
        return _checkedMembers.Count > 0 && IsSceneHost?.Invoke() == true && IsMemberInRoom();
    }

    /// <summary>
    /// Tells the partner the parts of the controllers here that went up, moved or went out since the last frame, while
    /// this game runs the room; and otherwise does what the scene host's words say whose time came.
    /// </summary>
    private void UpdateShownParts() {
        if (_shownControllers.Count == 0) {
            return;
        }

        var runsTheRoom = _checkedMembers.Count == 0 || IsSceneHost?.Invoke() == true || !IsMemberInRoom();
        var tells = TellsShownParts();

        _shownControllerList.Clear();
        _shownControllerList.AddRange(_shownControllers.Keys);
        foreach (var fsm in _shownControllerList) {
            if (!_shownControllers.TryGetValue(fsm, out var shown)) {
                continue;
            }

            if (fsm.GameObject is not { } controller || controller == null) {
                _shownControllers.Remove(fsm);
                continue;
            }

            if (runsTheRoom) {
                // Words of a scene host that this game took over from are no longer the room's
                shown.ForgetWords();
                WatchParts(fsm, controller.transform, shown, tells);
            } else {
                // What this game knew of its own parts is not what the host's are
                shown.Parts.Clear();
                shown.LastTold = -1f;
                ShowDueParts(fsm, shown);
                PutOutDueParts(shown);
            }
        }
    }

    /// <summary>
    /// Finds the parts of a controller here that went out, went up, moved or changed their flags since the last frame
    /// and tells the partner, if it tells. Those that went out first, so that a part that moves in the same frame
    /// doesn't carry one that is going out with it. A part seen for the first time is only noted.
    /// </summary>
    private void WatchParts(Fsm fsm, Transform controller, ShownController shown, bool tells) {
        var now = Time.time;
        var variables = fsm.Variables.GameObjectVariables;
        foreach (var variable in variables) {
            if (FindPart(variable, controller) is not { } part || !shown.Parts.TryGetValue(part, out var was) ||
                !was.IsUp || part.gameObject.activeInHierarchy) {
                continue;
            }

            if (tells) {
                TellPart(fsm, controller, shown, part, PartChange.WentOut, now - was.UpSince);
            }

            shown.Parts[part] = (false, part.position, was.UpSince, 0);
        }

        foreach (var variable in variables) {
            if (FindPart(variable, controller) is not { } part) {
                continue;
            }

            var isUp = part.gameObject.activeInHierarchy;
            var flags = isUp ? FlagsOf(part) : 0;
            var isKnown = shown.Parts.TryGetValue(part, out var was);
            if (tells && isUp && isKnown) {
                if (!was.IsUp) {
                    TellPart(fsm, controller, shown, part, PartChange.WentUp);
                } else if ((part.position - was.Place).sqrMagnitude > ShownPartMoveSqr || flags != was.Flags) {
                    TellPart(fsm, controller, shown, part, PartChange.Moved);
                }
            }

            shown.Parts[part] = (isUp, part.position, isKnown && was.IsUp ? was.UpSince : now, flags);
        }
    }

    /// <summary>
    /// The part of a controller that a variable of it holds, if it holds an object under the controller.
    /// </summary>
    private static Transform? FindPart(FsmGameObject? variable, Transform controller) {
        if (variable?.Value is not { } gameObject || gameObject == null) {
            return null;
        }

        var part = gameObject.transform;
        return part != controller && part.IsChildOf(controller) ? part : null;
    }

    /// <summary>
    /// The flags of the state machines of a part, a bit each, to see them change.
    /// </summary>
    private static int FlagsOf(Transform part) {
        var flags = 0;
        var bit = 0;
        part.GetComponents(PartMachines);
        foreach (var machine in PartMachines) {
            foreach (var flag in machine.FsmVariables.BoolVariables) {
                if (bit < 32 && flag.Value) {
                    flags |= 1 << bit;
                }

                bit++;
            }
        }

        PartMachines.Clear();
        return flags;
    }

    /// <summary>
    /// Hook on a state machine playing the particles of an object: a controller here whose parts the other game shows
    /// tells it, like the sparkle over the stage that comes before the flares.
    /// </summary>
    private void OnParticlePlay(Action<PlayParticleEmitter> orig, PlayParticleEmitter self) {
        orig(self);
        if (_shownControllers.Count == 0) {
            return;
        }

        try {
            if (self.Fsm is not { } fsm || !_shownControllers.TryGetValue(fsm, out var shown) || !TellsShownParts() ||
                fsm.GameObject is not { } controller || controller == null || self.gameObject == null ||
                fsm.GetOwnerDefaultTarget(self.gameObject) is not { } target || target == null) {
                return;
            }

            var part = target.transform;
            if (part != controller.transform && part.IsChildOf(controller.transform)) {
                TellPart(
                    fsm, controller.transform, shown, part, PartChange.Played, self.emit?.Value ?? 0,
                    self.resetIfPlaying ? 1f : 0f
                );
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Tells the partner what happened to a part of a controller here. A part that went up and has a state machine that
    /// hasn't started yet starts now, as it would before it first updates, so that what it comes to as it starts goes
    /// with the word.
    /// </summary>
    /// <param name="fsm">The controller.</param>
    /// <param name="controller">Its object.</param>
    /// <param name="shown">What is known of it.</param>
    /// <param name="part">The part.</param>
    /// <param name="change">What happened.</param>
    /// <param name="first">For particles played, how many were let out at once; for a part that went out, how long it
    /// was up.</param>
    /// <param name="second">For particles played, 1 if they start over when they play already.</param>
    private void TellPart(
        Fsm fsm,
        Transform controller,
        ShownController shown,
        Transform part,
        PartChange change,
        float first = 0f,
        float second = 0f
    ) {
        try {
            // A creature among the parts goes where its own sync puts it
            if (EntityProcessor.IsRegistered(part.gameObject)) {
                return;
            }

            if (change == PartChange.WentUp) {
                StartMachinesUnder(part);
            }

            var place = part.position;
            var word = new CoopSaveUpdate {
                TargetId = CoopTargets.Everyone,
                Kind = CoopSaveUpdateKind.MachinePart,
                Scene = controller.gameObject.scene.name,
                ObjectPath = ScenePath.Get(controller),
                FsmName = fsm.Name,
                StateName = PathUnder(part, controller),
                Part = (ushort) change,
                Sequence = ++_machineRoundCount,
                Values = [Time.time, place.x, place.y]
            };
            shown.LastTold = Time.time;

            var hasMachine = part.GetComponent<PlayMakerFSM>() != null;
            switch (change) {
                case PartChange.WentUp when hasMachine:
                    var size = part.localScale;
                    word.Values.Add(size.x);
                    word.Values.Add(size.y);
                    foreach (Transform piece in part) {
                        var piecePlace = piece.localPosition;
                        var pieceSize = piece.localScale;
                        word.Values.Add(piecePlace.x);
                        word.Values.Add(piecePlace.y);
                        word.Values.Add(pieceSize.x);
                        word.Values.Add(pieceSize.y);
                    }

                    TellFlags(word, part);
                    break;
                case PartChange.Moved when hasMachine:
                    TellFlags(word, part);
                    break;
                case PartChange.Played:
                    word.Values.Add(first);
                    word.Values.Add(second);
                    break;
                case PartChange.WentOut:
                    word.Values.Add(first);
                    break;
            }

            Send(word);
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Writes the flags of the state machines of a part in a word: their names in <see cref="CoopSaveUpdate.Names"/>,
    /// as the name of the state machine and of the flag on two lines, and 1 or 0 in
    /// <see cref="CoopSaveUpdate.Amounts"/>.
    /// </summary>
    private static void TellFlags(CoopSaveUpdate word, Transform part) {
        foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
            foreach (var flag in machine.FsmVariables.BoolVariables) {
                word.Names.Add(machine.FsmName + "\n" + flag.Name);
                word.Amounts.Add(flag.Value ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// Takes the scene host's word of what happened to a part of a controller, among the others in the order of the
    /// time in the host's game, to be done when its time comes. A word that came already is left.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnMachinePart(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() == true || update.Values.Count < 3) {
            return;
        }

        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
            FindMachineKind(target, update.FsmName) is not { ShowsParts: true } ||
            FindMachineFsm(target, update.FsmName) is not { } fsm) {
            return;
        }

        var shown = NoteShownController(fsm);

        // The words of another scene host go by its own clock
        if (shown.HostId != player.Id) {
            shown.ForgetWords();
            shown.HostId = player.Id;
        }

        // A word sent again comes once more; words may come in another order than they were sent, so each is known by
        // its number rather than by the newest
        if (!shown.Seen.Add(update.Sequence)) {
            return;
        }

        shown.SeenOrder.Enqueue(update.Sequence);
        if (shown.SeenOrder.Count > ShownPartSeenLimit) {
            shown.Seen.Remove(shown.SeenOrder.Dequeue());
        }

        var hostTime = update.Values[0];
        if (hostTime > shown.NewestHostTime) {
            shown.NewestHostTime = hostTime;
        }

        var index = shown.Due.Count;
        while (index > 0 && shown.Due[index - 1].HostTime > hostTime) {
            index--;
        }

        shown.Due.Insert(index, (hostTime, update));
    }

    /// <summary>
    /// Does what the scene host's words for a controller here say, for those whose time has come, in the order of the
    /// time in the host's game: each the time after the one before that it was there, unless that was a pause. A word
    /// much older than the newest is left, unless it puts a part out.
    /// </summary>
    private void ShowDueParts(Fsm fsm, ShownController shown) {
        var now = Time.time;
        while (shown.Due.Count > 0) {
            var (hostTime, word) = shown.Due[0];
            if ((PartChange) word.Part != PartChange.WentOut && shown.NewestHostTime - hostTime > ShownPartLateLimit) {
                shown.Due.RemoveAt(0);
                continue;
            }

            if (shown.HasShown) {
                var gap = hostTime - shown.LastShownHostTime;
                if (gap is > 0f and <= ShownPartSpacingLimit && shown.LastShownAt + gap > now) {
                    break;
                }
            }

            shown.Due.RemoveAt(0);
            shown.HasShown = true;
            shown.LastShownHostTime = hostTime;
            shown.LastShownAt = now;
            try {
                ShowPart(fsm, shown, word);
            } catch (Exception e) {
                LogInteractionError(e);
            }
        }
    }

    /// <summary>
    /// Does to a part of a controller here what the scene host's word says happened to the host's: puts it where the
    /// host's is, with its flags; for one that went up, puts it up here (see <see cref="PutPartUp"/>); plays its
    /// particles as the host's were; or puts it out once it was up here as long as the host's was.
    /// </summary>
    private void ShowPart(Fsm fsm, ShownController shown, CoopSaveUpdate word) {
        if (fsm.GameObject is not { } controller || controller == null ||
            controller.transform.Find(word.StateName) is not { } part ||
            EntityProcessor.IsRegistered(part.gameObject)) {
            return;
        }

        var values = word.Values;
        switch ((PartChange) word.Part) {
            case PartChange.Moved:
                ApplyFlags(part, word);
                part.position = new Vector3(values[1], values[2], part.position.z);
                return;
            case PartChange.WentUp:
                PutPartUp(shown, part, word);
                return;
            case PartChange.Played:
                part.position = new Vector3(values[1], values[2], part.position.z);
                if (values.Count < 5 || !part.TryGetComponent<ParticleSystem>(out var particles)) {
                    return;
                }

                if (values[3] >= 1f) {
                    particles.Emit((int) values[3]);
                    return;
                }

                if (values[4] > 0.5f && particles.isPlaying) {
                    particles.Stop();
                }

                particles.Play();
                return;
            case PartChange.WentOut:
                if (!part.gameObject.activeSelf || values.Count < 4) {
                    return;
                }

                // One that this game didn't put up for the host goes out at once
                if (!shown.UpHere.TryGetValue(part, out var since) || since + values[3] <= Time.time) {
                    shown.UpHere.Remove(part);
                    shown.OffAt.Remove(part);
                    part.gameObject.SetActive(false);
                    return;
                }

                shown.OffAt[part] = since + values[3];
                return;
        }
    }

    /// <summary>
    /// Puts a part of a controller here up as the scene host's went up: with the host's flags, where the host's stands,
    /// afresh if it is still up from before, started at once, and then as large and with its pieces where the host's
    /// came to as it started. The flags are given again once it is up, in case a state machine that is built as the
    /// part first wakes took the place of the one they were given to.
    /// </summary>
    private void PutPartUp(ShownController shown, Transform part, CoopSaveUpdate word) {
        ApplyFlags(part, word);
        var values = word.Values;
        part.position = new Vector3(values[1], values[2], part.position.z);
        if (part.gameObject.activeSelf) {
            part.gameObject.SetActive(false);
        }

        part.gameObject.SetActive(true);
        ApplyFlags(part, word);
        shown.UpHere[part] = Time.time;
        shown.OffAt.Remove(part);
        StartMachinesUnder(part);

        if (values.Count < 5) {
            return;
        }

        part.localScale = new Vector3(values[3], values[4], part.localScale.z);
        var index = 5;
        foreach (Transform piece in part) {
            if (index + 4 > values.Count) {
                break;
            }

            piece.localPosition = new Vector3(values[index], values[index + 1], piece.localPosition.z);
            piece.localScale = new Vector3(values[index + 2], values[index + 3], piece.localScale.z);
            index += 4;
        }
    }

    /// <summary>
    /// Gives the state machines of a part here the flags of the scene host's that a word carries.
    /// </summary>
    private static void ApplyFlags(Transform part, CoopSaveUpdate word) {
        for (var i = 0; i < word.Names.Count && i < word.Amounts.Count; i++) {
            var name = word.Names[i];
            var split = name.IndexOf('\n');
            if (split < 0) {
                continue;
            }

            var machineName = name[..split];
            var flagName = name[(split + 1)..];
            foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                if (machine.FsmName == machineName && machine.FsmVariables.FindFsmBool(flagName) is { } flag) {
                    flag.Value = word.Amounts[i] != 0;
                }
            }
        }
    }

    /// <summary>
    /// Puts out the parts of a controller here that were up as long as the scene host's were before they went out.
    /// </summary>
    private static void PutOutDueParts(ShownController shown) {
        if (shown.OffAt.Count == 0) {
            return;
        }

        var now = Time.time;
        List<Transform>? due = null;
        foreach (var pair in shown.OffAt) {
            if (pair.Value <= now) {
                (due ??= []).Add(pair.Key);
            }
        }

        if (due == null) {
            return;
        }

        foreach (var part in due) {
            shown.OffAt.Remove(part);
            shown.UpHere.Remove(part);
            if (part != null && part.gameObject.activeSelf) {
                part.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// The path of a part under its controller, by the names of the objects between them.
    /// </summary>
    private static string PathUnder(Transform part, Transform controller) {
        var names = new List<string>();
        for (var current = part; current != null && current != controller; current = current.parent) {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Forgets the controllers of the room that was left.
    /// </summary>
    private void ResetShownParts() {
        _shownControllers.Clear();
        _shownControllerList.Clear();
    }

    /// <summary>
    /// What is known of a controller whose parts the other game shows.
    /// </summary>
    private sealed class ShownController {
        /// <summary>
        /// In the game that runs the room, each part as it was last frame: whether it was up, where, since when in game
        /// time, and its flags.
        /// </summary>
        public readonly Dictionary<Transform, (bool IsUp, Vector3 Place, float UpSince, int Flags)> Parts = new();

        /// <summary>
        /// The scene host's words that wait here, in the order of the time in the host's game, with that time.
        /// </summary>
        public readonly List<(float HostTime, CoopSaveUpdate Word)> Due = [];

        /// <summary>
        /// The numbers of the host's words that came, to know one that comes again.
        /// </summary>
        public readonly HashSet<ulong> Seen = [];

        /// <summary>
        /// The numbers of <see cref="Seen"/> in the order they came, to forget the oldest.
        /// </summary>
        public readonly Queue<ulong> SeenOrder = new();

        /// <summary>
        /// In the other game, when each part was put up here for the host, in game time.
        /// </summary>
        public readonly Dictionary<Transform, float> UpHere = new();

        /// <summary>
        /// In the other game, the parts that go out once they were up here as long as the host's, with when.
        /// </summary>
        public readonly Dictionary<Transform, float> OffAt = new();

        /// <summary>
        /// The player whose words these are.
        /// </summary>
        public ushort HostId = ushort.MaxValue;

        /// <summary>
        /// The time in the host's game of the newest of its words that came.
        /// </summary>
        public float NewestHostTime = float.NegativeInfinity;

        /// <summary>
        /// Whether a word of the host was done here yet.
        /// </summary>
        public bool HasShown;

        /// <summary>
        /// The time in the host's game of the last word done here.
        /// </summary>
        public float LastShownHostTime;

        /// <summary>
        /// When the last word was done here, in game time.
        /// </summary>
        public float LastShownAt;

        /// <summary>
        /// When this game last told a part of it, in game time, or -1 for not yet.
        /// </summary>
        public float LastTold = -1f;

        /// <summary>
        /// Forgets the words of the host and what they did here.
        /// </summary>
        public void ForgetWords() {
            Due.Clear();
            Seen.Clear();
            SeenOrder.Clear();
            UpHere.Clear();
            OffAt.Clear();
            HostId = ushort.MaxValue;
            NewestHostTime = float.NegativeInfinity;
            HasShown = false;
        }
    }
}
