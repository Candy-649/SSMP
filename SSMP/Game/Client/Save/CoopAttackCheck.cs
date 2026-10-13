using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Compares, between the games of the players in the same room, when and where the things that can hurt a player come
/// up (USER 10-13, "先清查，再做对比"). A difference of a moment, like a burst that showed in one game where the partner
/// stood unharmed in the other, is over before anyone can see it, and the state of the room that
/// <see cref="CoopStateCheck"/> compares is the same again after it. So each game writes down every thing that can
/// hurt a player as it comes up, sends what it wrote down to the members in the room every second, and writes to the
/// log what came up in one game only, or in another place in each. Nothing of the game is changed.
///
/// A thing that can hurt a player is a DamageHero, which the game keeps a list of (DamageHero._damageHeroes); it comes
/// up when it, its object and its collider are all switched on, after being off for more than a moment. Each game knows
/// it by the same name: under the key of the thrown thing it is a part of (see <see cref="ThrownThings"/>), under the
/// path of the room's own object of the creature it is a part of (a scene client shows a copy of it, whose parts are
/// named the same), under what was spawned, or by its path in the room. The same thing comes up in two games a little
/// apart, as a copy follows the game that runs the room by the time a word takes, and a game stands still for a moment
/// when its player is hit; so the two count as one when they came up within a moment of each other, by the clocks of
/// the two games set side by side with the words between them.
/// </summary>
internal class CoopAttackCheck {
    /// <summary>
    /// How long after entering a room things are first watched, in seconds, so that the room has settled.
    /// </summary>
    private const float SettleTime = 2f;

    /// <summary>
    /// How often what came up goes to the members in the room, in seconds.
    /// </summary>
    private const float BatchInterval = 1f;

    /// <summary>
    /// How many things that came up one word carries at most. The rest are only counted, and the other game does not
    /// judge the time of that word.
    /// </summary>
    private const int MaxPerBatch = 100;

    /// <summary>
    /// How far apart in time the same thing may come up in two games, in seconds, besides the time a word takes there
    /// and back.
    /// </summary>
    private const float BaseWindow = 0.6f;

    /// <summary>
    /// The most that the time a word takes there and back widens <see cref="BaseWindow"/> by, in seconds.
    /// </summary>
    private const float MaxRoundTrip = 1f;

    /// <summary>
    /// How far apart the same thing may come up in two games, in units of the room. A copy of a creature that moves
    /// fast is a little behind, and a thing that flies is a little further along in one of them.
    /// </summary>
    private const float PlaceTolerance = 3f;

    /// <summary>
    /// How long a thing has to be off before it comes up again, in seconds, so that one that flickers counts once.
    /// </summary>
    private const float FlickerTime = 0.15f;

    /// <summary>
    /// How long what came up is kept for comparing, in seconds.
    /// </summary>
    private const float KeepTime = 20f;

    /// <summary>
    /// How often what came up is compared, in seconds.
    /// </summary>
    private const float JudgeInterval = 0.25f;

    /// <summary>
    /// How many readings of the difference between the clocks of this game and a member's are kept, the smallest of
    /// which counts: the one that took the least time on the way.
    /// </summary>
    private const int ClockReadings = 30;

    /// <summary>
    /// How long one look at everything that can hurt may take before it is written down, in milliseconds.
    /// </summary>
    private const double SlowLookMs = 1.0;

    /// <summary>
    /// How many names each list of the end of a visit shows.
    /// </summary>
    private const int MaxListed = 10;

    /// <summary>
    /// The net client for sending what came up.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other players, by ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The entity manager, which says whether this game runs the room.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// The IDs of the members of the checked save.
    /// </summary>
    private readonly Func<IReadOnlyCollection<ushort>> _getMembers;

    /// <summary>
    /// A number that this run of the game puts on its words, so that a member can tell when the game started over.
    /// </summary>
    private readonly uint _runId = (uint) new System.Random().Next();

    /// <summary>
    /// Whether each thing that can hurt was on at the last look, and when it went off.
    /// </summary>
    private readonly Dictionary<DamageHero, Watch> _watches = new();

    /// <summary>
    /// The things that can hurt that were destroyed, to forget after a look.
    /// </summary>
    private readonly List<DamageHero> _gone = [];

    /// <summary>
    /// When the current room was entered.
    /// </summary>
    private float _sceneEnteredAt;

    /// <summary>
    /// The room watched together with members, or null while none is.
    /// </summary>
    private string? _visitScene;

    /// <summary>
    /// When the watching of the room began.
    /// </summary>
    private float _visitStart;

    /// <summary>
    /// How many things came up here in the room.
    /// </summary>
    private int _visitCount;

    /// <summary>
    /// Whether a look took long, which is written down once a visit.
    /// </summary>
    private bool _saidSlow;

    /// <summary>
    /// The things that came up here, in the order they came up.
    /// </summary>
    private readonly List<Appearance> _here = [];

    /// <summary>
    /// The things that came up here since the last word.
    /// </summary>
    private readonly List<Appearance> _unsent = [];

    /// <summary>
    /// When the time that the next word covers began.
    /// </summary>
    private float _batchStart;

    /// <summary>
    /// When the next word goes.
    /// </summary>
    private float _nextBatch;

    /// <summary>
    /// The number of the last word.
    /// </summary>
    private uint _batchSeq;

    /// <summary>
    /// How many things came up since the last word that it could not carry.
    /// </summary>
    private int _dropped;

    /// <summary>
    /// The names that each member got the text of in this visit, by their hashes.
    /// </summary>
    private readonly Dictionary<ushort, HashSet<uint>> _namesSent = new();

    /// <summary>
    /// What each member's game said came up in the room, by their ID.
    /// </summary>
    private readonly Dictionary<ushort, Partner> _partners = new();

    /// <summary>
    /// The room that each member's game watched while this one watched another, by their ID.
    /// </summary>
    private readonly Dictionary<ushort, string> _otherScenes = new();

    /// <summary>
    /// How many things came up here in the room that the words could not carry.
    /// </summary>
    private int _visitDropped;

    /// <summary>
    /// The times of this game's words that could not carry all that came up here, by its clock: what came up in a
    /// member's game then is not judged, since what was left out here may have been it.
    /// </summary>
    private readonly List<(float From, float To)> _ownGaps = [];

    /// <summary>
    /// The members whose players are in the room of the local player, found each frame.
    /// </summary>
    private readonly List<ushort> _membersHere = [];

    /// <summary>
    /// When what came up is next compared.
    /// </summary>
    private float _nextJudge;

    /// <summary>
    /// The state machines of an object, read without making a new list each time.
    /// </summary>
    private readonly List<PlayMakerFSM> _fsms = [];

    /// <summary>
    /// Builds the lines of the log.
    /// </summary>
    private readonly StringBuilder _builder = new();

    /// <summary>
    /// Builds what is said of a thing as it comes up.
    /// </summary>
    private readonly StringBuilder _contextBuilder = new();

    /// <summary>
    /// Times a look at everything that can hurt.
    /// </summary>
    private readonly Stopwatch _stopwatch = new();

    /// <summary>
    /// Whether an error was written down in this room, so that it is written once.
    /// </summary>
    private bool _failed;

    public CoopAttackCheck(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        EntityManager entityManager,
        Func<IReadOnlyCollection<ushort>> getMembers
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _entityManager = entityManager;
        _getMembers = getMembers;
    }

    /// <summary>
    /// Starts watching rooms.
    /// </summary>
    public void RegisterHooks() {
        SceneManager.activeSceneChanged += OnSceneChanged;
        MonoBehaviourUtil.Instance.OnLateUpdateEvent += OnLateUpdate;
        ForgetRoom();
    }

    /// <summary>
    /// Stops watching rooms.
    /// </summary>
    public void DeregisterHooks() {
        SceneManager.activeSceneChanged -= OnSceneChanged;
        MonoBehaviourUtil.Instance.OnLateUpdateEvent -= OnLateUpdate;
        EndVisit();
        ForgetRoom();
    }

    /// <summary>
    /// Forgets the room that was left and starts waiting for the new one to settle.
    /// </summary>
    private void OnSceneChanged(Scene from, Scene to) {
        EndVisit();
        ForgetRoom();
    }

    /// <summary>
    /// Forgets everything watched in the current room.
    /// </summary>
    private void ForgetRoom() {
        _watches.Clear();
        _sceneEnteredAt = Time.unscaledTime;
        _failed = false;
    }

    /// <summary>
    /// Watches the room while members are in it, after everything of the frame moved.
    /// </summary>
    private void OnLateUpdate() {
        try {
            Update();
        } catch (Exception e) {
            Fail($"The watching of the room failed:\n{e}");
        }
    }

    /// <summary>
    /// Writes down an error the first time one happens in a room.
    /// </summary>
    private void Fail(string message) {
        if (!_failed) {
            _failed = true;
            Logger.Error($"[Attacks] {message}");
        }
    }

    /// <summary>
    /// The members whose players are in the room of the local player.
    /// </summary>
    private List<ushort> GetMembersHere() {
        _membersHere.Clear();
        foreach (var id in _getMembers()) {
            if (_playerData.TryGetValue(id, out var member) && member.IsInLocalScene) {
                _membersHere.Add(id);
            }
        }

        return _membersHere;
    }

    /// <summary>
    /// Watches what comes up, sends it and compares it, while members are in the room and it settled.
    /// </summary>
    private void Update() {
        var now = Time.unscaledTime;
        var hero = HeroController.SilentInstance;
        var members = GetMembersHere();
        if (members.Count == 0 || !_netClient.IsConnected || hero == null || hero.cState.transitioning ||
            now < _sceneEnteredAt + SettleTime) {
            EndVisit();
            return;
        }

        if (_visitScene == null) {
            StartVisit(now);
            return;
        }

        Look(now);

        if (now >= _nextBatch) {
            SendBatch(now, members);
        }

        if (now >= _nextJudge) {
            _nextJudge = now + JudgeInterval;
            Judge(now);
        }
    }

    /// <summary>
    /// Starts watching the room together with the members in it: what is on now counts as on from before, so that only
    /// what comes up from here on is compared.
    /// </summary>
    private void StartVisit(float now) {
        _visitScene = SceneManager.GetActiveScene().name;
        _visitStart = now;
        _visitCount = 0;
        _visitDropped = 0;
        _ownGaps.Clear();
        _saidSlow = false;
        _here.Clear();
        _unsent.Clear();
        _namesSent.Clear();
        _partners.Clear();
        _otherScenes.Clear();
        _batchStart = now;
        _nextBatch = now + BatchInterval;
        _nextJudge = now + JudgeInterval;
        _dropped = 0;

        _watches.Clear();
        foreach (var damager in DamageHero._damageHeroes.Values) {
            if (damager != null) {
                _watches[damager] = new Watch { On = IsOn(damager) };
            }
        }

        Logger.Info(
            $"[Attacks] Watching {_watches.Count} things that can hurt in {_visitScene} with the members in it"
        );
    }

    /// <summary>
    /// Whether a thing can hurt a player now: it, its object and its collider are switched on.
    /// </summary>
    private static bool IsOn(DamageHero damager) {
        if (!damager.isActiveAndEnabled) {
            return false;
        }

        var collider = damager.collider;
        return collider == null || collider.enabled;
    }

    /// <summary>
    /// Looks at every thing that can hurt, and writes down each that came up since the last look.
    /// </summary>
    private void Look(float now) {
        _stopwatch.Restart();
        foreach (var pair in DamageHero._damageHeroes) {
            var damager = pair.Value;
            if (damager == null) {
                if (!ReferenceEquals(damager, null)) {
                    _gone.Add(damager);
                }

                continue;
            }

            var on = IsOn(damager);
            if (!_watches.TryGetValue(damager, out var watch)) {
                // One made since the last look, which comes up if it is on
                watch = new Watch();
                _watches[damager] = watch;
            }

            if (on && !watch.On && (watch.OffAt < 0f || now - watch.OffAt >= FlickerTime)) {
                ComeUp(damager, now);
            } else if (!on && watch.On) {
                watch.OffAt = now;
            }

            watch.On = on;
        }

        // The game takes destroyed ones off its list itself; these are the ones it left
        foreach (var damager in _gone) {
            _watches.Remove(damager);
        }

        _gone.Clear();
        if (_watches.Count > DamageHero._damageHeroes.Count * 2 + 64) {
            ForgetDestroyedWatches();
        }

        _stopwatch.Stop();
        if (!_saidSlow && _stopwatch.Elapsed.TotalMilliseconds >= SlowLookMs) {
            _saidSlow = true;
            Logger.Info(
                $"[Attacks] One look at the {DamageHero._damageHeroes.Count} things that can hurt took " +
                $"{_stopwatch.Elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms"
            );
        }
    }

    /// <summary>
    /// Forgets the watches of things that were destroyed.
    /// </summary>
    private void ForgetDestroyedWatches() {
        foreach (var damager in _watches.Keys.ToList()) {
            if (damager == null) {
                _watches.Remove(damager!);
            }
        }
    }

    /// <summary>
    /// Writes down a thing that came up, unless it is a part of a player.
    /// </summary>
    private void ComeUp(DamageHero damager, float now) {
        if (IsOfAPlayer(damager.transform)) {
            return;
        }

        if (_unsent.Count >= MaxPerBatch) {
            _dropped++;
            _visitDropped++;
            return;
        }

        var key = KeyOf(damager.gameObject, out var what);
        var appearance = new Appearance(
            key, Hash(key), now, damager.transform.position, Describe(damager, what)
        );
        _here.Add(appearance);
        _unsent.Add(appearance);
        _visitCount++;
    }

    /// <summary>
    /// Whether an object is a part of the local player or of the figure of another player.
    /// </summary>
    private bool IsOfAPlayer(Transform transform) {
        // Under the hero itself: a hero that rides something is under it, and the root of that holds the room
        var hero = HeroController.SilentInstance;
        if (hero != null && transform.IsChildOf(hero.transform)) {
            return true;
        }

        foreach (var member in _playerData.Values) {
            if (member.PlayerContainer != null && transform.IsChildOf(member.PlayerContainer.transform)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The name of a thing that can hurt, which is the same in every game, and what it is a part of.
    /// </summary>
    private static string KeyOf(GameObject gameObject, out string what) {
        if (ThrownThings.TryGetMark(gameObject, out var mark)) {
            what = "a part of a thing that a creature threw";
            return $"thrown {mark.Key}/{PartsComponent.GetPath(mark.transform, gameObject.transform)}";
        }

        if (EntityProcessor.TryGetEntityObject(gameObject, out var root, out var roomObject)) {
            what = ReferenceEquals(root, roomObject)
                ? "a part of a creature run in this game"
                : "a part of the shown copy of a creature";
            return $"creature {ScenePath.Get(roomObject.transform)}|" +
                   PartsComponent.GetPath(root.transform, gameObject.transform);
        }

        // The outermost spawned object above it, whose name is the same in every game, unlike where it is kept
        Transform? spawned = null;
        for (var current = gameObject.transform; current != null; current = current.parent) {
            if (current.name.Contains("(Clone)")) {
                spawned = current;
            }
        }

        if (spawned != null) {
            what = "a part of something spawned";
            return $"spawned {WithoutClone(spawned.name)}/" +
                   WithoutClone(PartsComponent.GetPath(spawned, gameObject.transform));
        }

        what = "a part of the room";
        return $"room {gameObject.scene.name}|{ScenePath.Get(gameObject.transform)}";
    }

    /// <summary>
    /// A name without the mark that Unity puts on the objects it spawns.
    /// </summary>
    private static string WithoutClone(string name) {
        return name.Replace("(Clone)", "").Trim();
    }

    /// <summary>
    /// What is said of a thing as it came up, for the log: what it is a part of, how much it hurts, the states that the
    /// state machines of it and of what it is under were in, and where the players were.
    /// </summary>
    private string Describe(DamageHero damager, string what) {
        var text = _contextBuilder.Clear();
        text.Append(what).Append(", damage ").Append(damager.damageDealt).Append(' ').Append(damager.hazardType);

        var listed = 0;
        for (var current = damager.transform; current != null && listed < 3; current = current.parent) {
            current.GetComponents(_fsms);
            foreach (var fsm in _fsms) {
                if (listed++ == 3) {
                    break;
                }

                text.Append("; ");
                if (current != damager.transform) {
                    text.Append(current.name).Append(' ');
                }

                text.Append(fsm.FsmName).Append(" in ").Append(fsm.ActiveStateName);
            }
        }

        _fsms.Clear();

        var hero = HeroController.SilentInstance;
        if (hero != null) {
            text.Append("; this player at ").Append(Where(hero.transform.position));
        }

        foreach (var id in _getMembers()) {
            if (_playerData.TryGetValue(id, out var member) && member.IsInLocalScene && member.PlayerObject != null) {
                text.Append(", ").Append(member.Username).Append(" shown at ")
                    .Append(Where(member.PlayerObject.transform.position));
            }
        }

        return text.ToString();
    }

    #region Words between the games

    /// <summary>
    /// Sends the members in the room what came up since the last word, with the text of each name the member did not
    /// get yet. A word goes also when nothing came up, which tells the members that nothing did.
    /// </summary>
    private void SendBatch(float now, List<ushort> members) {
        foreach (var id in members) {
            if (!_namesSent.TryGetValue(id, out var sent)) {
                _namesSent[id] = sent = [];
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(_runId);
            writer.Write(_batchSeq + 1);
            writer.Write(_visitScene!);
            writer.Write(_batchStart);
            writer.Write(now);
            writer.Write((ushort) System.Math.Min(_dropped, ushort.MaxValue));

            var names = new List<Appearance>();
            foreach (var appearance in _unsent) {
                if (sent.Add(appearance.KeyHash)) {
                    names.Add(appearance);
                }
            }

            writer.Write((ushort) names.Count);
            foreach (var appearance in names) {
                writer.Write(appearance.KeyHash);
                writer.Write(appearance.Key);
            }

            writer.Write((ushort) _unsent.Count);
            foreach (var appearance in _unsent) {
                writer.Write(appearance.KeyHash);
                writer.Write((ushort) Mathf.Clamp((appearance.Time - _batchStart) * 1000f, 0f, ushort.MaxValue));
                writer.Write(ToShort(appearance.Place.x));
                writer.Write(ToShort(appearance.Place.y));
            }

            writer.Flush();
            var data = stream.ToArray();
            if (data.Length > ushort.MaxValue) {
                Fail($"What came up was too long to send ({data.Length} bytes)");
                continue;
            }

            _netClient.UpdateManager.SetCoopCheckUpdate(new CoopCheckUpdate {
                TargetId = id,
                Kind = CoopCheckKind.Attacks,
                Data = data
            });
        }

        if (_dropped > 0) {
            _ownGaps.Add((_batchStart, now));
        }

        _batchSeq++;
        _unsent.Clear();
        _dropped = 0;
        _batchStart = now;
        _nextBatch = now + BatchInterval;
    }

    /// <summary>
    /// Takes what came up in the game of a member in the same room.
    /// </summary>
    public void OnAttacks(CoopCheckUpdate update) {
        if (_visitScene == null || !_getMembers().Contains(update.PlayerId)) {
            return;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(update.Data));
            var runId = reader.ReadUInt32();
            var seq = reader.ReadUInt32();
            var scene = reader.ReadString();
            var start = reader.ReadSingle();
            var sentAt = reader.ReadSingle();
            var dropped = reader.ReadUInt16();

            if (scene != _visitScene) {
                _otherScenes[update.PlayerId] = scene;
                return;
            }

            if (!_partners.TryGetValue(update.PlayerId, out var partner) || partner.RunId != runId) {
                partner = new Partner(runId, start);
                _partners[update.PlayerId] = partner;
            }

            if (!partner.Seqs.Add(seq)) {
                return;
            }

            partner.Batches++;
            partner.ClockReadings.Enqueue(Time.unscaledTime - sentAt);
            if (partner.ClockReadings.Count > ClockReadings) {
                partner.ClockReadings.Dequeue();
            }

            partner.Offset = partner.ClockReadings.Min();

            // Each word starts where the one before ended, so one that starts later followed a time that the member's
            // game did not watch the room, like while its player was away from it with others still here
            if (partner.Batches > 1 && start > partner.CoveredUntil + BatchInterval / 2f) {
                partner.Gaps.Add((partner.CoveredUntil, start));
            }

            partner.Start = System.Math.Min(partner.Start, start);
            partner.CoveredUntil = System.Math.Max(partner.CoveredUntil, sentAt);
            if (dropped > 0) {
                partner.Gaps.Add((start, sentAt));
                partner.Dropped += dropped;
            }

            var names = reader.ReadUInt16();
            for (var i = 0; i < names; i++) {
                var hash = reader.ReadUInt32();
                partner.Names[hash] = reader.ReadString();
            }

            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++) {
                var hash = reader.ReadUInt32();
                var time = start + reader.ReadUInt16() / 1000f;
                var place = new Vector2(FromShort(reader.ReadInt16()), FromShort(reader.ReadInt16()));
                partner.Theirs.Add(new TheirAppearance(hash, time, place));
                partner.Count++;
            }
        } catch (Exception e) {
            Fail($"Could not read what came up in the game of a member:\n{e}");
        }
    }

    /// <summary>
    /// A coordinate of a place in tenths of a unit, as a word carries it.
    /// </summary>
    private static short ToShort(float value) {
        return (short) Mathf.Clamp(Mathf.Round(value * 10f), short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// A coordinate of a place from tenths of a unit.
    /// </summary>
    private static float FromShort(short value) {
        return value / 10f;
    }

    #endregion

    #region Comparing

    /// <summary>
    /// How far apart in time the same thing may come up in two games now, in seconds.
    /// </summary>
    private float Window() {
        return BaseWindow + Mathf.Min(_netClient.UpdateManager.AverageRtt / 1000f, MaxRoundTrip);
    }

    /// <summary>
    /// Compares what came up here with what came up in the game of each member in the room: the two that came up
    /// within a moment of each other count as one, and what is left on one side once the other side can no longer
    /// have it is written to the log.
    /// </summary>
    private void Judge(float now) {
        var window = Window();
        foreach (var pair in _partners) {
            var partner = pair.Value;
            if (partner.Batches == 0) {
                continue;
            }

            Match(pair.Key, partner, window);
            JudgeTheirs(pair.Key, partner, now, window);
            JudgeOurs(pair.Key, partner, window);
            partner.Theirs.RemoveAll(theirs => theirs.Done && theirs.Time + partner.Offset < now - KeepTime);
        }

        _here.RemoveAll(ours => ours.Time < now - KeepTime);
    }

    /// <summary>
    /// Pairs each thing that came up in the game of a member with the first one of the same name that came up here
    /// within a moment of it, and writes down a pair that came up far apart.
    /// </summary>
    private void Match(ushort id, Partner partner, float window) {
        foreach (var theirs in partner.Theirs) {
            if (theirs.Done) {
                continue;
            }

            var at = theirs.Time + partner.Offset;
            Appearance? found = null;
            for (var i = FirstHereFrom(at - window); i < _here.Count; i++) {
                var ours = _here[i];
                if (ours.Time > at + window) {
                    break;
                }

                if (ours.KeyHash == theirs.KeyHash && !ours.IsDoneFor(id)) {
                    found = ours;
                    break;
                }
            }

            if (found == null) {
                continue;
            }

            theirs.Done = true;
            found.SetDoneFor(id);
            partner.Matched++;
            var distance = Vector2.Distance(found.Place, theirs.Place);
            if (distance > PlaceTolerance) {
                OnMoved(id, partner, found, theirs, distance, found.Time - at);
            }
        }
    }

    /// <summary>
    /// The index of the first thing that came up here at or after a time. What came up here is in the order it came up.
    /// </summary>
    private int FirstHereFrom(float time) {
        var low = 0;
        var high = _here.Count;
        while (low < high) {
            var middle = (low + high) / 2;
            if (_here[middle].Time < time) {
                low = middle + 1;
            } else {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Writes down each thing that came up in the game of a member and not here, once nothing here can pair with it any
    /// more. One from before this game watched the room, or from a time this game's word could not carry all of, is
    /// left alone.
    /// </summary>
    private void JudgeTheirs(ushort id, Partner partner, float now, float window) {
        foreach (var theirs in partner.Theirs) {
            if (theirs.Done) {
                continue;
            }

            var at = theirs.Time + partner.Offset;
            if (now < at + window + JudgeInterval) {
                continue;
            }

            theirs.Done = true;
            if (at >= _visitStart + window && !IsInGap(_ownGaps, at, window)) {
                OnOnlyThere(id, partner, theirs, at);
            }
        }
    }

    /// <summary>
    /// Writes down each thing that came up here and not in the game of a member, once the member's words cover the
    /// time it could have come up there. One from before the member's game watched the room, or from a time its word
    /// could not carry all of, is left alone.
    /// </summary>
    private void JudgeOurs(ushort id, Partner partner, float window) {
        var covered = partner.CoveredUntil + partner.Offset;
        foreach (var ours in _here) {
            if (ours.IsDoneFor(id)) {
                continue;
            }

            if (covered < ours.Time + window) {
                break;
            }

            ours.SetDoneFor(id);
            var theirTime = ours.Time - partner.Offset;
            if (theirTime >= partner.Start + window && !IsInGap(partner.Gaps, theirTime, window)) {
                OnOnlyHere(id, partner, ours);
            }
        }
    }

    /// <summary>
    /// Whether a time falls in one of the times that a game's words did not cover in full, by that game's clock.
    /// </summary>
    private static bool IsInGap(List<(float From, float To)> gaps, float time, float window) {
        foreach (var (from, to) in gaps) {
            if (time >= from - window && time <= to + window) {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region The log

    /// <summary>
    /// Writes down a thing that came up here and not in the game of a member: in full the first time in the visit.
    /// </summary>
    private void OnOnlyHere(ushort id, Partner partner, Appearance ours) {
        if (Count(partner.OnlyHere, ours.Key) > 1) {
            return;
        }

        _builder.Clear();
        _builder.Append("[Attacks] Only in this game in ").Append(_visitScene).Append(", not in ").Append(NameOf(id))
            .Append("'s: ").Append(ours.Key)
            .AppendLine().Append("    came up at ").Append(Where(ours.Place)).Append(", ")
            .Append(Seconds(ours.Time - _visitStart)).Append(" into the visit")
            .AppendLine().Append("    ").Append(ours.Context);
        AppendNearest(partner.Theirs, ours.KeyHash, ours.Time - partner.Offset, ours.Place, "in the partner's game");
        AppendRunner();
        Logger.Info(_builder.ToString());
    }

    /// <summary>
    /// Writes down a thing that came up in the game of a member and not here: in full the first time in the visit.
    /// </summary>
    private void OnOnlyThere(ushort id, Partner partner, TheirAppearance theirs, float at) {
        var name = partner.Names.TryGetValue(theirs.KeyHash, out var text) ? text : $"#{theirs.KeyHash:X8}";
        if (Count(partner.OnlyThere, name) > 1) {
            return;
        }

        _builder.Clear();
        _builder.Append("[Attacks] Only in ").Append(NameOf(id)).Append("'s game in ").Append(_visitScene)
            .Append(", not in this one: ").Append(name)
            .AppendLine().Append("    came up there at ").Append(Where(theirs.Place)).Append(", about ")
            .Append(Seconds(at - _visitStart)).Append(" into the visit by this game's clock");
        AppendNearest(_here, theirs.KeyHash, at, theirs.Place, "here");
        AppendRunner();
        Logger.Info(_builder.ToString());
    }

    /// <summary>
    /// Writes down a thing that came up in both games far apart: in full the first time in the visit.
    /// </summary>
    private void OnMoved(
        ushort id,
        Partner partner,
        Appearance ours,
        TheirAppearance theirs,
        float distance,
        float apart
    ) {
        if (!partner.Moved.TryGetValue(ours.Key, out var moved)) {
            moved = new Moved();
            partner.Moved[ours.Key] = moved;
        }

        moved.Count++;
        moved.Farthest = Mathf.Max(moved.Farthest, distance);
        if (moved.Count > 1) {
            return;
        }

        _builder.Clear();
        _builder.Append("[Attacks] In another place in ").Append(_visitScene).Append(" than in ").Append(NameOf(id))
            .Append("'s game: ").Append(ours.Key)
            .AppendLine().Append("    here at ").Append(Where(ours.Place)).Append(", there at ")
            .Append(Where(theirs.Place)).Append(", ").Append(distance.ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" apart, ").Append(Milliseconds(apart)).Append(" from there to here")
            .AppendLine().Append("    ").Append(ours.Context);
        AppendRunner();
        Logger.Info(_builder.ToString());
    }

    /// <summary>
    /// Adds to a line of the log the nearest in time of the same name on the other side, which tells a thing that came
    /// up late from one that did not come up at all.
    /// </summary>
    private void AppendNearest<T>(List<T> list, uint keyHash, float time, Vector2 place, string where)
        where T : class, ISeen {
        T? nearest = null;
        foreach (var seen in list) {
            if (seen.KeyHash == keyHash &&
                (nearest == null || System.Math.Abs(seen.Time - time) < System.Math.Abs(nearest.Time - time))) {
                nearest = seen;
            }
        }

        _builder.AppendLine().Append("    ");
        if (nearest == null) {
            _builder.Append("nothing of that name came up ").Append(where).Append(" in the last ")
                .Append(Seconds(KeepTime));
            return;
        }

        _builder.Append("the nearest of that name ").Append(where).Append(" came up ")
            .Append(Milliseconds(nearest.Time - time)).Append(" from it, ")
            .Append(Vector2.Distance(nearest.Place, place).ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" away");
    }

    /// <summary>
    /// Adds to a line of the log whether this game runs the room.
    /// </summary>
    private void AppendRunner() {
        _builder.AppendLine().Append("    this game runs the room: ").Append(_entityManager.IsSceneHost ? "yes" : "no");
    }

    /// <summary>
    /// Counts a name in a tally and gives its count.
    /// </summary>
    private static int Count(Dictionary<string, int> tally, string name) {
        tally.TryGetValue(name, out var count);
        tally[name] = ++count;
        return count;
    }

    /// <summary>
    /// The name of a member for the log.
    /// </summary>
    private string NameOf(ushort id) {
        return _playerData.TryGetValue(id, out var member) ? member.Username : $"player {id}";
    }

    /// <summary>
    /// Stops watching the room, and writes down what the comparison with each member found while they were in it
    /// together.
    /// </summary>
    private void EndVisit() {
        if (_visitScene == null) {
            return;
        }

        var scene = _visitScene;
        var now = Time.unscaledTime;
        foreach (var pair in _otherScenes) {
            if (!_partners.ContainsKey(pair.Key)) {
                Logger.Info($"[Attacks] {NameOf(pair.Key)}'s game watched {pair.Value} while this one watched {scene}");
            }
        }

        foreach (var pair in _partners) {
            var partner = pair.Value;
            _builder.Clear();
            _builder.Append("[Attacks] Left ").Append(scene).Append(" after ").Append(Seconds(now - _visitStart))
                .Append(" with ").Append(NameOf(pair.Key)).Append(": ").Append(_visitCount).Append(" came up here, ")
                .Append(partner.Count).Append(" there, ").Append(partner.Matched).Append(" of them in both");
            if (partner.Dropped > 0 || _visitDropped > 0) {
                _builder.Append(", too many to send: ").Append(_visitDropped).Append(" here, ").Append(partner.Dropped)
                    .Append(" there");
            }

            AppendTally("only in this game", partner.OnlyHere);
            AppendTally($"only in {NameOf(pair.Key)}'s game", partner.OnlyThere);
            if (partner.Moved.Count > 0) {
                _builder.AppendLine().Append("    in another place in each:");
                foreach (var moved in partner.Moved.OrderByDescending(moved => moved.Value.Count).Take(MaxListed)) {
                    _builder.AppendLine().Append("      ").Append(moved.Key).Append(" x").Append(moved.Value.Count)
                        .Append(", up to ")
                        .Append(moved.Value.Farthest.ToString("0.0", CultureInfo.InvariantCulture)).Append(" apart");
                }
            }

            Logger.Info(_builder.ToString());
        }

        if (_partners.Count == 0 && _otherScenes.Count == 0 && now - _visitStart >= 5f) {
            Logger.Info($"[Attacks] Nothing came from the members' games in {scene} for {Seconds(now - _visitStart)}");
        }

        _visitScene = null;
        _here.Clear();
        _unsent.Clear();
        _partners.Clear();
        _otherScenes.Clear();
        _namesSent.Clear();
    }

    /// <summary>
    /// Adds a tally of names to the line of the end of a visit, the most counted first.
    /// </summary>
    private void AppendTally(string what, Dictionary<string, int> tally) {
        if (tally.Count == 0) {
            return;
        }

        _builder.AppendLine().Append("    ").Append(what).Append(':');
        foreach (var pair in tally.OrderByDescending(pair => pair.Value).Take(MaxListed)) {
            _builder.AppendLine().Append("      ").Append(pair.Key).Append(" x").Append(pair.Value);
        }

        if (tally.Count > MaxListed) {
            _builder.AppendLine().Append("      and ").Append(tally.Count - MaxListed).Append(" more names");
        }
    }

    /// <summary>
    /// A number of seconds as text.
    /// </summary>
    private static string Seconds(float seconds) {
        return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// A number of seconds as text in milliseconds, with its sign.
    /// </summary>
    private static string Milliseconds(float seconds) {
        return (seconds * 1000f).ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ms";
    }

    /// <summary>
    /// A place in the room as text.
    /// </summary>
    private static string Where(Vector2 position) {
        return $"({position.x.ToString("0.0", CultureInfo.InvariantCulture)}, " +
               $"{position.y.ToString("0.0", CultureInfo.InvariantCulture)})";
    }

    #endregion

    /// <summary>
    /// The FNV-1a hash of a text, which is the same in every game, unlike the hash of a string in .NET.
    /// </summary>
    private static uint Hash(string text) {
        var hash = 2166136261u;
        foreach (var character in text) {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }

    /// <summary>
    /// Whether a thing that can hurt was on at the last look, and when it last went off.
    /// </summary>
    private sealed class Watch {
        /// <summary>
        /// Whether it was on.
        /// </summary>
        public bool On;

        /// <summary>
        /// When it last went off, or less than 0 if it did not since it was first seen.
        /// </summary>
        public float OffAt = -1f;
    }

    /// <summary>
    /// A thing that came up in a game: its name, when and where.
    /// </summary>
    private interface ISeen {
        /// <summary>
        /// The hash of its name.
        /// </summary>
        uint KeyHash { get; }

        /// <summary>
        /// When it came up, by the clock of the game it came up in.
        /// </summary>
        float Time { get; }

        /// <summary>
        /// Where it came up.
        /// </summary>
        Vector2 Place { get; }
    }

    /// <summary>
    /// A thing that came up here.
    /// </summary>
    private sealed class Appearance(string key, uint keyHash, float time, Vector2 place, string context) : ISeen {
        /// <summary>
        /// Its name.
        /// </summary>
        public string Key { get; } = key;

        /// <inheritdoc />
        public uint KeyHash { get; } = keyHash;

        /// <inheritdoc />
        public float Time { get; } = time;

        /// <inheritdoc />
        public Vector2 Place { get; } = place;

        /// <summary>
        /// What was said of it as it came up.
        /// </summary>
        public string Context { get; } = context;

        /// <summary>
        /// The members whose games it was compared with: paired with one of theirs or written down.
        /// </summary>
        private List<ushort>? _doneFor;

        /// <summary>
        /// Whether it was compared with the game of a member.
        /// </summary>
        public bool IsDoneFor(ushort id) {
            return _doneFor != null && _doneFor.Contains(id);
        }

        /// <summary>
        /// Marks it as compared with the game of a member.
        /// </summary>
        public void SetDoneFor(ushort id) {
            (_doneFor ??= []).Add(id);
        }
    }

    /// <summary>
    /// A thing that came up in the game of a member.
    /// </summary>
    private sealed class TheirAppearance(uint keyHash, float time, Vector2 place) : ISeen {
        /// <inheritdoc />
        public uint KeyHash { get; } = keyHash;

        /// <inheritdoc />
        public float Time { get; } = time;

        /// <inheritdoc />
        public Vector2 Place { get; } = place;

        /// <summary>
        /// Whether it was paired with one here or written down.
        /// </summary>
        public bool Done { get; set; }
    }

    /// <summary>
    /// How often a thing came up in another place in each game, and how far apart at most.
    /// </summary>
    private sealed class Moved {
        /// <summary>
        /// How often.
        /// </summary>
        public int Count;

        /// <summary>
        /// How far apart at most, in units of the room.
        /// </summary>
        public float Farthest;
    }

    /// <summary>
    /// What the game of a member said came up in the room, and what the comparison with it found.
    /// </summary>
    private sealed class Partner(uint runId, float start) {
        /// <summary>
        /// The number that the run of the member's game puts on its words.
        /// </summary>
        public uint RunId { get; } = runId;

        /// <summary>
        /// When the member's game began to watch the room, as far as its words say, by its clock.
        /// </summary>
        public float Start { get; set; } = start;

        /// <summary>
        /// Until when the member's words cover what came up, by its clock.
        /// </summary>
        public float CoveredUntil { get; set; } = start;

        /// <summary>
        /// What the clock of this game is ahead of the member's by, as a word took the least time on the way.
        /// </summary>
        public float Offset { get; set; }

        /// <summary>
        /// The last readings of what the clock of this game is ahead of the member's by, with the time on the way.
        /// </summary>
        public Queue<float> ClockReadings { get; } = new();

        /// <summary>
        /// The numbers of the words taken, so that one that comes twice is taken once.
        /// </summary>
        public HashSet<uint> Seqs { get; } = [];

        /// <summary>
        /// How many words of this room came.
        /// </summary>
        public int Batches { get; set; }

        /// <summary>
        /// The text of the names, by their hashes.
        /// </summary>
        public Dictionary<uint, string> Names { get; } = new();

        /// <summary>
        /// What came up in the member's game and is yet to be compared, or was compared a short while ago.
        /// </summary>
        public List<TheirAppearance> Theirs { get; } = [];

        /// <summary>
        /// The times that the member's words did not cover in full, by the clock of its game: words that could not
        /// carry all that came up, and times between words while it did not watch the room.
        /// </summary>
        public List<(float From, float To)> Gaps { get; } = [];

        /// <summary>
        /// How many things came up in the member's game in the room, as its words carried them.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// How many things that came up its words could not carry.
        /// </summary>
        public int Dropped { get; set; }

        /// <summary>
        /// How many things came up in both games.
        /// </summary>
        public int Matched { get; set; }

        /// <summary>
        /// How often each name came up only here.
        /// </summary>
        public Dictionary<string, int> OnlyHere { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// How often each name came up only in the member's game.
        /// </summary>
        public Dictionary<string, int> OnlyThere { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// How often each name came up in another place in each game.
        /// </summary>
        public Dictionary<string, Moved> Moved { get; } = new(StringComparer.Ordinal);
    }
}
