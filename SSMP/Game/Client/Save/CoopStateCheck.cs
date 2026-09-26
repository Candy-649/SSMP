using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Compares what the two games of a checked two-player save have in the room that both players are in, and writes down
/// every difference that lasts. Nothing of the game is changed.
///
/// Each game reads the state of the room every second: the state that each state machine of the objects of the room
/// is in, what the objects that take hits keep of them, the saved objects of the world, the creatures and the story
/// flags. Every few seconds it sends the partner a digest of the values that stayed the same for a while, in groups,
/// and where the digests of a group differ the two games compare the values of that group one by one. A value that
/// differs while it stayed the same in both games for a while is not a hit or a change that is still on its way. It is
/// written to the log with the value of each game, how long each game has had it, and the hits and touches that went
/// between the games for that object (see <see cref="CoopHits.GetTraffic"/>).
///
/// Some values differ between the two games by their nature, like a state machine that notices where the player of its
/// own game stands or one that the player who talks to it moves on. Those are only listed when the players leave the
/// room, with a word on why they may differ.
/// </summary>
internal class CoopStateCheck {
    /// <summary>
    /// How long after entering a room its state is first read, in seconds, so that it has settled.
    /// </summary>
    private const float SettleTime = 4f;

    /// <summary>
    /// How often the state of the room is read, in seconds.
    /// </summary>
    private const float SampleInterval = 1f;

    /// <summary>
    /// How often a digest goes to the partner, in seconds.
    /// </summary>
    private const float DigestInterval = 4f;

    /// <summary>
    /// How long a value has to stay the same in both games before a difference counts, in seconds. A hit, a touch or a
    /// change of the world takes a moment to reach the other game, and a value that differs only until it arrives is
    /// not a difference.
    /// </summary>
    private const float StableTime = 5f;

    /// <summary>
    /// The number of groups that the values are put in for the digest, by their names.
    /// </summary>
    private const int BucketCount = 128;

    /// <summary>
    /// How many groups one request asks about at most, which keeps the answers small.
    /// </summary>
    private const int MaxBucketsPerRequest = 3;

    /// <summary>
    /// How long a group whose digests still differ in the same way is not asked about again, in seconds. Asking again
    /// would only get the same answer.
    /// </summary>
    private const float ReaskTime = 60f;

    /// <summary>
    /// How old an answer to a request may be when it arrives, in seconds. The values in an older one may have changed
    /// in the other game since, so it is dropped and its groups are asked about again.
    /// </summary>
    private const float MaxAnswerAge = 2f;

    /// <summary>
    /// How long a request that was not answered is kept, in seconds.
    /// </summary>
    private const float RequestLifetime = 10f;

    /// <summary>
    /// How much time finding the values of a room may take per frame, in milliseconds.
    /// </summary>
    private const double FindBudgetMs = 1.0;

    /// <summary>
    /// How much time reading the values of the room may take per frame, in milliseconds.
    /// </summary>
    private const double SampleBudgetMs = 0.5;

    /// <summary>
    /// How far back the hits and touches of an object are shown with a difference, in seconds.
    /// </summary>
    private const float TrafficTime = 120f;

    /// <summary>
    /// How many of the differences that may be expected are listed when the players leave a room.
    /// </summary>
    private const int MaxListedExpected = 20;

    /// <summary>
    /// How many of the values that only one game has are listed when the players leave a room.
    /// </summary>
    private const int MaxListedOneSided = 8;

    /// <summary>
    /// The layer of the objects that notice only the player of their own game.
    /// </summary>
    private const int HeroDetectorLayer = 13;

    /// <summary>
    /// What the value of a state machine starts with while it or its object is switched off.
    /// </summary>
    private const string OffPrefix = "(off)";

    /// <summary>
    /// What the names of the values of the story flags start with.
    /// </summary>
    private const string StoryPrefix = "story ";

    /// <summary>
    /// The layers of the player character and of its hit box, which a touch of the player comes from.
    /// </summary>
    private static readonly int[] PlayerLayers = [9, 20];

    /// <summary>
    /// The tags of the player character and of its hit box.
    /// </summary>
    private static readonly string[] PlayerTags = ["Player", "HeroBox"];

    /// <summary>
    /// The names of the fields of an object that takes hits which hold what the hits did to it, like whether it broke
    /// or how many hits it took. The backing field of a property counts by the name of the property.
    /// </summary>
    private static readonly Regex StateFieldName = new(
        "^(isBroken|broken|activated|isActivated|hits|hitsLeft|hitCount|isCut|isDead|isOpen|opened|isOpened|State|" +
        "state|currentState|stage|currentStage|isSpent|spent|isUsed|used|isTriggered|triggered|isDone|done|isLit|lit|" +
        "isDestroyed|destroyed)$"
    );

    /// <summary>
    /// The fields that hold the state of objects that take hits, by type.
    /// </summary>
    private static readonly Dictionary<Type, StateField[]> StateFields = new();

    /// <summary>
    /// The fields of an action that say which touches it listens to, by type.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> TouchFilterFields = new();

    /// <summary>
    /// The net client for sending comparisons.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other players, by ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The entity manager, which has the creatures of the room.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// The two-player save, which knows the saved objects of the world that both saves share.
    /// </summary>
    private readonly CoopSave _coopSave;

    /// <summary>
    /// Gets the ID of the partner that the two-player save was checked with, or null outside a checked save.
    /// </summary>
    private readonly Func<ushort?> _getPartnerId;

    /// <summary>
    /// A number that tells the digests of this run of the game from those of an earlier run, whose count restarted.
    /// </summary>
    private readonly uint _runId = (uint) new System.Random().Next();

    /// <summary>
    /// Measures how much time finding and reading the values of the room take in a frame.
    /// </summary>
    private readonly Stopwatch _stopwatch = new();

    /// <summary>
    /// The values of the room, by name.
    /// </summary>
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The state machines of the room, with their values.
    /// </summary>
    private readonly List<(PlayMakerFSM Fsm, Entry Entry)> _fsms = [];

    /// <summary>
    /// The objects of the room that take hits, with the fields that hold their state and their values.
    /// </summary>
    private readonly List<(MonoBehaviour Part, StateField[] Fields, Entry Entry)> _parts = [];

    /// <summary>
    /// The saved objects of the world in the room that both saves share, with their values.
    /// </summary>
    private readonly List<(PersistentBoolItem Item, Entry Entry)> _items = [];

    /// <summary>
    /// The names of the values of the creatures of the room, by the ID of their entity.
    /// </summary>
    private readonly Dictionary<ushort, string> _creatureKeys = new();

    /// <summary>
    /// The names of the values of the story flags, in the order of <see cref="CoopSave.GetStoryFields"/>.
    /// </summary>
    private string[]? _storyKeys;

    /// <summary>
    /// The paths of the objects of the room that were worked out, as <see cref="ScenePath.Get"/> gives them. All the
    /// children of a parent are worked out at once, since each is numbered by the earlier ones of the same name.
    /// </summary>
    private readonly Dictionary<Transform, string> _paths = new();

    /// <summary>
    /// How many earlier siblings have each name, while the paths of the children of one parent are worked out.
    /// </summary>
    private readonly Dictionary<string, int> _nameCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// The components of one object, while an object is looked at.
    /// </summary>
    private readonly List<Component> _components = [];

    /// <summary>
    /// The components of the room that are still to be looked at while its values are found.
    /// </summary>
    private readonly List<MonoBehaviour> _toFind = [];

    /// <summary>
    /// How many of <see cref="_toFind"/> were looked at.
    /// </summary>
    private int _findIndex;

    /// <summary>
    /// The root objects of the loaded scenes that are still to be searched for objects that take hits.
    /// </summary>
    private readonly Queue<GameObject> _rootsToSearch = new();

    /// <summary>
    /// The objects that take hits under one root object, while it is searched.
    /// </summary>
    private readonly List<IHitResponder> _responders = [];

    /// <summary>
    /// Whether finding the values of the room started.
    /// </summary>
    private bool _findStarted;

    /// <summary>
    /// Whether the values of the room were found.
    /// </summary>
    private bool _found;

    /// <summary>
    /// When the current room was entered, in unscaled time.
    /// </summary>
    private float _sceneEnteredAt;

    /// <summary>
    /// The number of the current reading of the room, which tells values that went away from those that are there.
    /// </summary>
    private int _sample;

    /// <summary>
    /// Whether a reading of the room is going on, which is spread over frames.
    /// </summary>
    private bool _sampling;

    /// <summary>
    /// How many of the state machines, objects that take hits and saved objects the current reading has read.
    /// </summary>
    private int _sampleCursor;

    /// <summary>
    /// When the room was first read in full, in unscaled time, or a negative number before. No value can have stayed
    /// the same for a while before then, so digests start a while after it.
    /// </summary>
    private float _firstSampleAt;

    /// <summary>
    /// When the room is read next and when a digest goes out next.
    /// </summary>
    private float _nextSample, _nextDigest;

    /// <summary>
    /// The number of the last digest sent.
    /// </summary>
    private uint _digestSeq;

    /// <summary>
    /// The number of the last request sent.
    /// </summary>
    private uint _requestSeq;

    /// <summary>
    /// The run and the number of the last digest of the partner that was compared.
    /// </summary>
    private uint _partnerRunId, _partnerDigestSeq;

    /// <summary>
    /// The requests that were not answered yet, by their numbers.
    /// </summary>
    private readonly Dictionary<uint, Request> _requests = new();

    /// <summary>
    /// For each group, the local digest and the digest of the partner when it was last asked about.
    /// </summary>
    private readonly ushort[] _askedHere = new ushort[BucketCount], _askedThere = new ushort[BucketCount];

    /// <summary>
    /// When each group was last asked about, in unscaled time, or a negative number if it wasn't yet.
    /// </summary>
    private readonly float[] _askedAt = new float[BucketCount];

    /// <summary>
    /// Where the next request starts looking for groups that differ, so that every group gets its turn.
    /// </summary>
    private int _bucketRotation;

    /// <summary>
    /// The sums of each group of the local values, filled in when needed.
    /// </summary>
    private readonly uint[] _buckets = new uint[BucketCount];

    /// <summary>
    /// The differences that were written down and are still there, by the name of the value.
    /// </summary>
    private readonly Dictionary<string, Difference> _open = new(StringComparer.Ordinal);

    /// <summary>
    /// The differences of story flags that were written down since connecting, as the name and both values. The story
    /// is the same in every room, so a difference of it is written down once rather than in every room.
    /// </summary>
    private readonly HashSet<string> _writtenStoryDifferences = new(StringComparer.Ordinal);

    /// <summary>
    /// The scene that both players are in together, or null while they are not.
    /// </summary>
    private string? _visitScene;

    /// <summary>
    /// When the players came together in the room, in unscaled time.
    /// </summary>
    private float _visitStart;

    /// <summary>
    /// How many digests were sent and compared, how many groups were asked about and how many differences were written
    /// down while the players are together in the room.
    /// </summary>
    private int _visitDigestsSent, _visitDigestsCompared, _visitBucketsAsked, _visitDifferences;

    /// <summary>
    /// The scene of the last digest of the partner that was about another scene than the local one, or null if none
    /// was while the players are together in the room.
    /// </summary>
    private string? _visitOtherScene;

    /// <summary>
    /// The differences in the room that may be expected, by the name of the value.
    /// </summary>
    private readonly Dictionary<string, Difference> _visitExpected = new(StringComparer.Ordinal);

    /// <summary>
    /// How many times each value differed in a new way while the players are together in the room, by its name.
    /// </summary>
    private readonly Dictionary<string, int> _visitDiffered = new(StringComparer.Ordinal);

    /// <summary>
    /// The values that only the local game has.
    /// </summary>
    private readonly HashSet<string> _visitOnlyHere = new(StringComparer.Ordinal);

    /// <summary>
    /// The values that only the game of the partner has.
    /// </summary>
    private readonly HashSet<string> _visitOnlyThere = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether an error was written down in this room already, which happens once per room.
    /// </summary>
    private bool _failed;

    /// <summary>
    /// Builds the text of values that take a few parts.
    /// </summary>
    private readonly StringBuilder _builder = new();

    public CoopStateCheck(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        EntityManager entityManager,
        CoopSave coopSave,
        Func<ushort?> getPartnerId
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _entityManager = entityManager;
        _coopSave = coopSave;
        _getPartnerId = getPartnerId;
    }

    /// <summary>
    /// Starts reading and comparing rooms.
    /// </summary>
    public void RegisterHooks() {
        SceneManager.activeSceneChanged += OnSceneChanged;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        ForgetRoom();
        _writtenStoryDifferences.Clear();
    }

    /// <summary>
    /// Stops reading and comparing rooms.
    /// </summary>
    public void DeregisterHooks() {
        SceneManager.activeSceneChanged -= OnSceneChanged;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        EndVisit();
        ForgetRoom();
    }

    /// <summary>
    /// Handles a comparison from the partner.
    /// </summary>
    public void OnCoopCheckUpdate(CoopCheckUpdate update) {
        if (_visitScene == null || _getPartnerId() != update.PlayerId) {
            return;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(update.Data));
            switch (update.Kind) {
                case CoopCheckKind.Digest:
                    OnDigest(reader);
                    break;
                case CoopCheckKind.DetailRequest:
                    OnDetailRequest(reader, update.PlayerId);
                    break;
                case CoopCheckKind.Detail:
                    OnDetail(reader);
                    break;
            }
        } catch (Exception e) {
            Fail($"Could not read a comparison of the room from the partner:\n{e}");
        }
    }

    /// <summary>
    /// Forgets the room that was left and starts waiting for the new one to settle.
    /// </summary>
    private void OnSceneChanged(Scene from, Scene to) {
        EndVisit();
        ForgetRoom();
    }

    /// <summary>
    /// Forgets everything read of the current room.
    /// </summary>
    private void ForgetRoom() {
        _entries.Clear();
        _fsms.Clear();
        _parts.Clear();
        _items.Clear();
        _creatureKeys.Clear();
        _paths.Clear();
        _open.Clear();
        _toFind.Clear();
        _rootsToSearch.Clear();
        _findIndex = 0;
        _findStarted = false;
        _found = false;
        _sceneEnteredAt = Time.unscaledTime;
        _sample = 0;
        _sampling = false;
        _sampleCursor = 0;
        _firstSampleAt = -1f;
        _failed = false;
    }

    /// <summary>
    /// Reads and compares the room while both players are in it.
    /// </summary>
    private void OnUpdate() {
        try {
            Update();
        } catch (Exception e) {
            Fail($"The comparison of the room failed:\n{e}");
        }
    }

    /// <summary>
    /// Writes down an error the first time one happens in a room.
    /// </summary>
    private void Fail(string message) {
        if (!_failed) {
            _failed = true;
            Logger.Error($"[State check] {message}");
        }
    }

    /// <summary>
    /// Finds the values of the room once it settled, then reads them and sends digests while both players are in it.
    /// </summary>
    private void Update() {
        var now = Time.unscaledTime;
        var hero = HeroController.SilentInstance;
        if (_getPartnerId() is not { } partnerId || !_netClient.IsConnected ||
            !_playerData.TryGetValue(partnerId, out var partner) || !partner.IsInLocalScene || hero == null ||
            hero.cState.transitioning || now < _sceneEnteredAt + SettleTime) {
            EndVisit();
            return;
        }

        if (!_found) {
            FindSome();
            return;
        }

        if (_visitScene == null) {
            StartVisit(now);
        }

        if (!_sampling && now >= _nextSample) {
            _sampling = true;
            _sampleCursor = 0;
            _sample++;
            _nextSample = now + SampleInterval;
        }

        if (_sampling) {
            SampleSome(now);
        }

        if (now >= _nextDigest && HasSettledValues(now)) {
            _nextDigest = now + DigestInterval;
            SendDigest(partnerId, now);
        }
    }

    /// <summary>
    /// Whether the room was read long enough ago for its values to have stayed the same for a while.
    /// </summary>
    private bool HasSettledValues(float now) {
        return _firstSampleAt >= 0f && now >= _firstSampleAt + StableTime;
    }

    /// <summary>
    /// Whether the time that a frame may spend on the comparison is up.
    /// </summary>
    private bool IsOutOfTime(double budgetMs) {
        return _stopwatch.Elapsed.TotalMilliseconds >= budgetMs;
    }

    #region Finding the values of the room

    /// <summary>
    /// Looks at the next components of the room for values to compare, for as long as a frame may spend on it. It
    /// starts with the state machines and the saved objects, and then searches the root objects of the loaded scenes
    /// one at a time for the objects that take hits.
    /// </summary>
    private void FindSome() {
        _stopwatch.Restart();
        if (!_findStarted) {
            _findStarted = true;
            _toFind.AddRange(
                Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            );
            _toFind.AddRange(
                Object.FindObjectsByType<PersistentBoolItem>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            );

            for (var i = 0; i < SceneManager.sceneCount; i++) {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) {
                    foreach (var root in scene.GetRootGameObjects()) {
                        _rootsToSearch.Enqueue(root);
                    }
                }
            }
        }

        while (!IsOutOfTime(FindBudgetMs)) {
            if (_findIndex < _toFind.Count) {
                try {
                    Find(_toFind[_findIndex++]);
                } catch (Exception e) {
                    Fail($"Could not look at an object of the room:\n{e}");
                }

                continue;
            }

            if (_rootsToSearch.Count > 0) {
                var root = _rootsToSearch.Dequeue();
                if (root != null) {
                    root.GetComponentsInChildren(true, _responders);
                    foreach (var responder in _responders) {
                        if (responder is MonoBehaviour behaviour) {
                            _toFind.Add(behaviour);
                        }
                    }
                }

                continue;
            }

            _toFind.Clear();
            _found = true;
            return;
        }
    }

    /// <summary>
    /// Adds the value of one component of the room, if it is one that is compared.
    /// </summary>
    private void Find(MonoBehaviour? behaviour) {
        if (behaviour == null) {
            return;
        }

        var scene = behaviour.gameObject.scene;
        if (!scene.IsValid() || scene.name == "DontDestroyOnLoad") {
            return;
        }

        switch (behaviour) {
            case PlayMakerFSM fsm:
                if (IsCompared(fsm)) {
                    var path = GetPath(fsm.transform);
                    var key = $"fsm {scene.name}|{path}|{fsm.FsmName}{GetFsmIndex(fsm)}";
                    if (TryAddEntry(key, scene.name, path, fsm, out var entry)) {
                        _fsms.Add((fsm, entry));
                    }
                }

                break;
            case PersistentBoolItem item:
                if (_coopSave.IsSharedWorldItem(item, out var itemScene, out var id)) {
                    var itemKey = $"saved {itemScene}|{id}";
                    if (TryAddEntry(itemKey, scene.name, GetPath(item.transform), null, out var itemEntry)) {
                        _items.Add((item, itemEntry));
                    }
                }

                break;
            case IHitResponder when IsCompared(behaviour):
                var partPath = GetPath(behaviour.transform);
                var partKey = $"part {scene.name}|{partPath}|{behaviour.GetType().Name}{GetPartIndex(behaviour)}";
                if (TryAddEntry(partKey, scene.name, partPath, null, out var partEntry)) {
                    _parts.Add((behaviour, GetStateFields(behaviour.GetType()), partEntry));
                }

                break;
        }
    }

    /// <summary>
    /// Whether a component of the room is compared: it is part of the room itself, which both games have in the same
    /// place (see <see cref="CoopHits.IsRoomObject"/>), and not of an object that is there for each player alone, nor
    /// in a place that is (see <see cref="PersonalPlaces"/>).
    /// </summary>
    private bool IsCompared(MonoBehaviour behaviour) {
        if (!CoopHits.IsRoomObject(behaviour) || PersonalPlaces.Contains(behaviour.gameObject)) {
            return false;
        }

        behaviour.GetComponents(_components);
        foreach (var component in _components) {
            if (component != null && CoopHits.IsPersonal(component.GetType())) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the path of an object of the room, working out the paths of all its siblings at once.
    /// </summary>
    private string GetPath(Transform transform) {
        if (_paths.TryGetValue(transform, out var path)) {
            return path;
        }

        var parent = transform.parent;
        var parentPath = parent != null ? GetPath(parent) : null;

        _nameCounts.Clear();
        if (parent == null) {
            foreach (var root in transform.gameObject.scene.GetRootGameObjects()) {
                _paths[root.transform] = GetIndexedName(root.name);
            }
        } else {
            for (var i = 0; i < parent.childCount; i++) {
                var child = parent.GetChild(i);
                _paths[child] = parentPath + "/" + GetIndexedName(child.name);
            }
        }

        return _paths.TryGetValue(transform, out path) ? path : ScenePath.Get(transform);
    }

    /// <summary>
    /// The name of an object, followed by the number of earlier siblings with the same name if there are any, as
    /// <see cref="ScenePath"/> writes it.
    /// </summary>
    private string GetIndexedName(string name) {
        _nameCounts.TryGetValue(name, out var index);
        _nameCounts[name] = index + 1;
        return index == 0 ? name : $"{name} [{index}]";
    }

    /// <summary>
    /// The number that tells a state machine apart from earlier ones of the same name on its object, like "#1", or
    /// nothing for the first.
    /// </summary>
    private string GetFsmIndex(PlayMakerFSM fsm) {
        var index = 0;
        fsm.GetComponents(typeof(PlayMakerFSM), _components);
        foreach (var other in _components) {
            if (other == fsm) {
                break;
            }

            if (other is PlayMakerFSM { } same && same.FsmName == fsm.FsmName) {
                index++;
            }
        }

        return index == 0 ? "" : $"#{index}";
    }

    /// <summary>
    /// The number that tells a component apart from earlier ones of the same type on its object, like "#1", or nothing
    /// for the first.
    /// </summary>
    private string GetPartIndex(MonoBehaviour behaviour) {
        behaviour.GetComponents(behaviour.GetType(), _components);
        var index = _components.IndexOf(behaviour);
        return index <= 0 ? "" : $"#{index}";
    }

    /// <summary>
    /// Adds a value of the room under a name, unless a value of that name was added already.
    /// </summary>
    private bool TryAddEntry(string key, string? scene, string? path, PlayMakerFSM? fsm, out Entry entry) {
        if (_entries.ContainsKey(key)) {
            entry = null!;
            return false;
        }

        entry = new Entry(key, scene, path) { Fsm = fsm };
        _entries[key] = entry;
        return true;
    }

    /// <summary>
    /// Why a value may differ between the games without anything being wrong, or null if it should not. It is worked
    /// out when the value first differs, and for a state machine only once it has started: the actions of one that
    /// has not started yet are not loaded, and loading them early would break it.
    /// </summary>
    private static string? GetExpectedReason(Entry entry) {
        if (entry.ExpectedKnown || entry.Fsm == null) {
            return entry.Expected;
        }

        var fsm = entry.Fsm;
        if (fsm.Fsm is not { Initialized: true }) {
            return null;
        }

        entry.Expected = GetExpectedReason(fsm);
        entry.ExpectedKnown = true;
        return entry.Expected;
    }

    /// <summary>
    /// Why a state machine may be in another state in the other game without anything being wrong, or null if it
    /// should not be: it notices where the player of its own game stands, it is talked to or used by one player at a
    /// time, or it picks what it does at random.
    /// </summary>
    private static string? GetExpectedReason(PlayMakerFSM fsm) {
        if (fsm.gameObject.layer == HeroDetectorLayer) {
            return "notices where this game's player is";
        }

        if (fsm.GetComponentInParent<NPCControlBase>(true) != null ||
            fsm.GetComponentInParent<InteractableBase>(true) != null) {
            return "talked to or used by one player";
        }

        var random = false;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action == null) {
                    continue;
                }

                var type = action.GetType();
                if (ListensForThePlayer(action, type)) {
                    return "notices where this game's player is";
                }

                random |= type.Name.Contains("Random");
            }
        }

        return random ? "picks at random" : null;
    }

    /// <summary>
    /// Whether an action listens for touches of the player character, by the tag or layer it filters them by.
    /// </summary>
    private static bool ListensForThePlayer(FsmStateAction action, Type type) {
        if (!TouchFilterFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Public | BindingFlags.Instance),
                field => field.Name is "collideTag" or "collideLayer"
            );
            TouchFilterFields[type] = fields;
        }

        foreach (var field in fields) {
            switch (field.GetValue(action)) {
                case FsmString { IsNone: false } tag when Array.IndexOf(PlayerTags, tag.Value) >= 0:
                case FsmInt { IsNone: false } layer when Array.IndexOf(PlayerLayers, layer.Value) >= 0:
                case int number when Array.IndexOf(PlayerLayers, number) >= 0:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the fields of a type of object that takes hits which hold what the hits did to it (see
    /// <see cref="StateFieldName"/>), including those of its base types.
    /// </summary>
    private static StateField[] GetStateFields(Type type) {
        if (StateFields.TryGetValue(type, out var fields)) {
            return fields;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.DeclaredOnly;
        var found = new List<StateField>();
        for (var current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType) {
            foreach (var field in current.GetFields(flags)) {
                var name = field.Name;
                if (name.StartsWith("<") && name.IndexOf('>') > 1) {
                    name = name.Substring(1, name.IndexOf('>') - 1);
                }

                if ((field.FieldType == typeof(bool) || field.FieldType == typeof(int) || field.FieldType.IsEnum) &&
                    StateFieldName.IsMatch(name)) {
                    found.Add(new StateField(field, name));
                }
            }
        }

        fields = found.ToArray();
        StateFields[type] = fields;
        return fields;
    }

    #endregion

    #region Reading the values of the room

    /// <summary>
    /// Reads the next values of the room, for as long as a frame may spend on it. At the end of the room it reads the
    /// creatures and the story flags, and forgets the values that went away.
    /// </summary>
    private void SampleSome(float now) {
        _stopwatch.Restart();

        var total = _fsms.Count + _parts.Count + _items.Count;
        while (_sampleCursor < total) {
            var index = _sampleCursor++;
            try {
                if (index < _fsms.Count) {
                    var (fsm, entry) = _fsms[index];
                    SetValue(entry, ReadFsm(fsm), now);
                } else if (index < _fsms.Count + _parts.Count) {
                    var (part, fields, entry) = _parts[index - _fsms.Count];
                    SetValue(entry, ReadPart(part, fields), now);
                } else {
                    var (item, entry) = _items[index - _fsms.Count - _parts.Count];
                    SetValue(entry, ReadItem(item), now);
                }
            } catch (Exception e) {
                Fail($"Could not read a value of the room:\n{e}");
            }

            if (IsOutOfTime(SampleBudgetMs)) {
                return;
            }
        }

        try {
            ReadCreatures(now);
        } catch (Exception e) {
            Fail($"Could not read the creatures of the room:\n{e}");
        }

        try {
            ReadStory(now);
        } catch (Exception e) {
            Fail($"Could not read the story flags:\n{e}");
        }

        ForgetGoneValues();

        _sampling = false;
        if (_firstSampleAt < 0f) {
            _firstSampleAt = now;
        }
    }

    /// <summary>
    /// Forgets the values that the reading that just ended did not read, which belong to creatures that went away, and
    /// the differences written down for them.
    /// </summary>
    private void ForgetGoneValues() {
        List<string>? gone = null;
        foreach (var entry in _entries.Values) {
            if (entry.Sample != _sample) {
                (gone ??= []).Add(entry.Key);
            }
        }

        if (gone == null) {
            return;
        }

        foreach (var key in gone) {
            _entries.Remove(key);
            if (_open.Remove(key)) {
                Logger.Info($"[State check] No longer there in {_visitScene}: {key}");
            }
        }
    }

    /// <summary>
    /// Sets the value that a name has now, remembering when it changed.
    /// </summary>
    private void SetValue(Entry entry, string value, float now) {
        if (entry.Sample == 0) {
            entry.Value = value;
            entry.ValueHash = Hash(value);
            entry.Since = now;
        } else if (!string.Equals(entry.Value, value, StringComparison.Ordinal)) {
            entry.Previous = entry.Value;
            entry.Value = value;
            entry.ValueHash = Hash(value);
            entry.Since = now;
        }

        entry.Sample = _sample;
    }

    /// <summary>
    /// The state of a state machine, after "(off) " while it or its object is switched off.
    /// </summary>
    private static string ReadFsm(PlayMakerFSM fsm) {
        if (fsm == null) {
            return "(gone)";
        }

        var state = fsm.ActiveStateName ?? "";
        return fsm.enabled && fsm.gameObject.activeInHierarchy ? state : OffPrefix + " " + state;
    }

    /// <summary>
    /// Whether an object that takes hits is switched on, with what its fields keep of the hits.
    /// </summary>
    private string ReadPart(MonoBehaviour part, StateField[] fields) {
        if (part == null) {
            return "(gone)";
        }

        _builder.Clear();
        _builder.Append(part.isActiveAndEnabled ? "on" : "off");
        foreach (var field in fields) {
            _builder.Append(", ").Append(field.Name).Append('=');
            switch (field.Field.GetValue(part)) {
                case bool flag:
                    _builder.Append(flag ? "true" : "false");
                    break;
                case int number:
                    _builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    break;
                case { } other:
                    _builder.Append(other);
                    break;
            }
        }

        return _builder.ToString();
    }

    /// <summary>
    /// Whether a saved object of the world is set. This reads what the object last worked out, which the game and the
    /// two-player save keep up to date, rather than making it work its value out again, which runs its own code.
    /// </summary>
    private static string ReadItem(PersistentBoolItem item) {
        if (item == null) {
            return "(gone)";
        }

        return item.ItemData is { Value: true } ? "set" : "not set";
    }

    /// <summary>
    /// Reads whether each creature of the room is there and what health it has, from the object that this game shows
    /// of it: its own on the scene host, the copy on the scene client.
    /// </summary>
    private void ReadCreatures(float now) {
        foreach (var entity in _entityManager.ActiveEntities) {
            var host = entity.Object.Host;
            var client = entity.Object.Client;
            if (!_creatureKeys.TryGetValue(entity.Id, out var key)) {
                var sceneName = host != null ? host.scene.name : client != null ? client.scene.name : "";
                key = $"creature {sceneName}|{entity.Id}";
                _creatureKeys[entity.Id] = key;
            }

            if (!_entries.TryGetValue(key, out var entry)) {
                var named = host != null ? host : client;
                entry = new Entry(key, null, named != null ? ScenePath.Get(named.transform) : null);
                _entries[key] = entry;
            }

            var shown = client != null && client.activeInHierarchy ? client
                : host != null && host.activeInHierarchy ? host
                : null;
            string value;
            if (shown == null) {
                value = OffPrefix;
            } else {
                var healthManager = shown.GetComponent<HealthManager>();
                value = healthManager == null ? "on"
                    : healthManager.GetIsDead() ? "dead"
                    : "hp " + healthManager.hp.ToString(CultureInfo.InvariantCulture);
            }

            SetValue(entry, value, now);
        }
    }

    /// <summary>
    /// Reads the story flags that both saves share (see <see cref="CoopSave.GetStoryFields"/>).
    /// </summary>
    private void ReadStory(float now) {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        var fields = CoopSave.GetStoryFields();
        if (_storyKeys == null || _storyKeys.Length != fields.Length) {
            _storyKeys = new string[fields.Length];
            for (var i = 0; i < fields.Length; i++) {
                _storyKeys[i] = StoryPrefix + fields[i].Name;
            }
        }

        for (var i = 0; i < fields.Length; i++) {
            if (!_entries.TryGetValue(_storyKeys[i], out var entry)) {
                entry = new Entry(_storyKeys[i], null, null);
                _entries[_storyKeys[i]] = entry;
            }

            var value = CoopSave.ReadStoryValue(fields[i], playerData);
            if (entry.Sample == 0 || entry.Number != value) {
                entry.Number = value;
                SetValue(entry, value.ToString(CultureInfo.InvariantCulture), now);
            } else {
                entry.Sample = _sample;
            }
        }
    }

    #endregion

    #region Comparing with the partner

    /// <summary>
    /// Starts comparing the room with the game of the partner.
    /// </summary>
    private void StartVisit(float now) {
        _visitScene = SceneManager.GetActiveScene().name;
        _visitStart = now;
        _visitDigestsSent = 0;
        _visitDigestsCompared = 0;
        _visitBucketsAsked = 0;
        _visitDifferences = 0;
        _visitOtherScene = null;
        _visitExpected.Clear();
        _visitDiffered.Clear();
        _visitOnlyHere.Clear();
        _visitOnlyThere.Clear();
        _requests.Clear();
        for (var i = 0; i < BucketCount; i++) {
            _askedAt[i] = -1f;
        }

        _nextSample = now;
        _nextDigest = now + SampleInterval;
    }

    /// <summary>
    /// Stops comparing the room, and writes down what the comparison found while the players were in it together.
    /// </summary>
    private void EndVisit() {
        if (_visitScene == null) {
            return;
        }

        var scene = _visitScene;
        _visitScene = null;
        _requests.Clear();

        var now = Time.unscaledTime;
        if (_visitDigestsCompared == 0) {
            if (_visitOtherScene != null) {
                Logger.Info(
                    $"[State check] The partner's game compared {_visitOtherScene} while this one was in {scene}, " +
                    $"for {Seconds(now - _visitStart)}"
                );
            } else if (_visitDigestsSent >= 3) {
                Logger.Info(
                    $"[State check] Nothing came from the partner's game in {scene} for " +
                    $"{Seconds(now - _visitStart)}, after {_visitDigestsSent} digests were sent to it"
                );
            }

            _open.Clear();
            return;
        }

        _builder.Clear();
        _builder.Append("[State check] Left ").Append(scene).Append(" after ").Append(Seconds(now - _visitStart))
            .Append(" together: ").Append(_entries.Count).Append(" values, ").Append(_visitDigestsCompared)
            .Append(" digests compared, ").Append(_visitBucketsAsked).Append(" groups looked into, ")
            .Append(_visitDifferences).Append(" differences written down");

        var again = 0;
        foreach (var pair in _visitDiffered) {
            if (pair.Value > 1) {
                if (again++ == 0) {
                    _builder.AppendLine().Append("    differed again after that:");
                }

                _builder.AppendLine().Append("      ").Append(pair.Key).Append(": ").Append(pair.Value)
                    .Append(" times");
            }
        }

        if (_open.Count > 0) {
            _builder.AppendLine().Append("    still different when leaving:");
            foreach (var pair in _open) {
                _builder.AppendLine().Append("      ").Append(pair.Key).Append(": here \"").Append(pair.Value.Here)
                    .Append("\", partner \"").Append(pair.Value.There).Append('"');
            }
        }

        if (_visitExpected.Count > 0) {
            _builder.AppendLine().Append("    ").Append(_visitExpected.Count).Append(" that may be expected:");
            var listed = 0;
            foreach (var pair in _visitExpected) {
                if (listed++ == MaxListedExpected) {
                    _builder.AppendLine().Append("      and ").Append(_visitExpected.Count - MaxListedExpected)
                        .Append(" more");
                    break;
                }

                _builder.AppendLine().Append("      ").Append(pair.Key).Append(" [").Append(pair.Value.Reason)
                    .Append("]: here \"").Append(pair.Value.Here).Append("\", partner \"").Append(pair.Value.There)
                    .Append('"');
            }
        }

        AppendOneSided("only in this game", _visitOnlyHere);
        AppendOneSided("only in the partner's game", _visitOnlyThere);
        _open.Clear();

        Logger.Info(_builder.ToString());
    }

    /// <summary>
    /// Adds the values that only one of the games has to the text of the end of a comparison.
    /// </summary>
    private void AppendOneSided(string what, HashSet<string> keys) {
        if (keys.Count == 0) {
            return;
        }

        _builder.AppendLine().Append("    ").Append(keys.Count).Append(' ').Append(what).Append(':');
        var listed = 0;
        foreach (var key in keys) {
            if (listed++ == MaxListedOneSided) {
                _builder.AppendLine().Append("      and ").Append(keys.Count - MaxListedOneSided).Append(" more");
                break;
            }

            _builder.AppendLine().Append("      ").Append(key);
        }
    }

    /// <summary>
    /// Works out the sum of each group of the local values that stayed the same for a while.
    /// </summary>
    private void ComputeBuckets(float now) {
        Array.Clear(_buckets, 0, BucketCount);
        foreach (var entry in _entries.Values) {
            if (now - entry.Since >= StableTime) {
                _buckets[entry.Bucket] += Mix(entry.KeyHash, entry.ValueHash);
            }
        }
    }

    /// <summary>
    /// Sends the partner the digest of each group of the local values.
    /// </summary>
    private void SendDigest(ushort partnerId, float now) {
        ComputeBuckets(now);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_runId);
        writer.Write(++_digestSeq);
        writer.Write(_visitScene ?? "");
        foreach (var bucket in _buckets) {
            writer.Write(Fold(bucket));
        }

        writer.Flush();
        Send(partnerId, CoopCheckKind.Digest, stream.ToArray());
        _visitDigestsSent++;
    }

    /// <summary>
    /// Compares a digest of the partner with the local values, and asks about the groups that differ.
    /// </summary>
    private void OnDigest(BinaryReader reader) {
        var runId = reader.ReadUInt32();
        var seq = reader.ReadUInt32();
        var scene = reader.ReadString();
        var now = Time.unscaledTime;
        if (scene != _visitScene) {
            _visitOtherScene = scene;
            return;
        }

        if (runId == _partnerRunId && seq <= _partnerDigestSeq || !HasSettledValues(now)) {
            return;
        }

        _partnerRunId = runId;
        _partnerDigestSeq = seq;

        ComputeBuckets(now);
        _visitDigestsCompared++;

        var theirs = new ushort[BucketCount];
        for (var i = 0; i < BucketCount; i++) {
            theirs[i] = reader.ReadUInt16();
        }

        // A difference that was written down is gone once its group is the same again with the value settled here
        List<string>? settled = null;
        foreach (var pair in _open) {
            if (_entries.TryGetValue(pair.Key, out var entry) && theirs[entry.Bucket] == Fold(_buckets[entry.Bucket]) &&
                now - entry.Since >= StableTime) {
                (settled ??= []).Add(pair.Key);
            }
        }

        if (settled != null) {
            foreach (var key in settled) {
                WriteSettled(key, now);
            }
        }

        ForgetOldRequests(now);

        // The groups that differ in a way they didn't when they were last asked about, in turn from where the last
        // request stopped. A group that still differs in the same way would only get the same answer.
        List<int>? asked = null;
        for (var n = 0; n < BucketCount && (asked?.Count ?? 0) < MaxBucketsPerRequest; n++) {
            var bucket = (_bucketRotation + n) % BucketCount;
            var here = Fold(_buckets[bucket]);
            if (theirs[bucket] == here || _askedAt[bucket] >= 0f && _askedHere[bucket] == here &&
                _askedThere[bucket] == theirs[bucket] && now < _askedAt[bucket] + ReaskTime) {
                continue;
            }

            (asked ??= []).Add(bucket);
            _askedHere[bucket] = here;
            _askedThere[bucket] = theirs[bucket];
            _askedAt[bucket] = now;
        }

        if (asked == null || _getPartnerId() is not { } partnerId) {
            return;
        }

        _bucketRotation = (asked[asked.Count - 1] + 1) % BucketCount;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var request = new Request(now, asked);
        var requestId = ++_requestSeq;
        writer.Write(requestId);
        writer.Write(_visitScene ?? "");
        writer.Write((byte) asked.Count);
        foreach (var bucket in asked) {
            var entries = GetStableEntries(bucket, now);
            writer.Write((byte) bucket);
            writer.Write((ushort) entries.Count);
            foreach (var entry in entries) {
                writer.Write(entry.KeyHash);
                writer.Write(entry.ValueHash);
                request.Sent[entry.KeyHash] = entry.ValueHash;
            }
        }

        writer.Flush();
        _requests[requestId] = request;
        _visitBucketsAsked += asked.Count;
        Send(partnerId, CoopCheckKind.DetailRequest, stream.ToArray());
    }

    /// <summary>
    /// Forgets the requests that were not answered for a while, so that their groups are asked about again.
    /// </summary>
    private void ForgetOldRequests(float now) {
        List<uint>? old = null;
        foreach (var pair in _requests) {
            if (now - pair.Value.SentAt > RequestLifetime) {
                (old ??= []).Add(pair.Key);
            }
        }

        if (old == null) {
            return;
        }

        foreach (var requestId in old) {
            if (_requests.Remove(requestId, out var request)) {
                AskAgain(request);
            }
        }
    }

    /// <summary>
    /// Lets the groups of a request that got no usable answer be asked about again.
    /// </summary>
    private void AskAgain(Request request) {
        foreach (var bucket in request.Buckets) {
            _askedAt[bucket] = -1f;
        }
    }

    /// <summary>
    /// Answers a request of the partner with the local values of the groups that it asks about which differ from its
    /// own, with their names, and the names that the partner has and the local game does not.
    /// </summary>
    private void OnDetailRequest(BinaryReader reader, ushort partnerId) {
        var requestId = reader.ReadUInt32();
        var scene = reader.ReadString();
        if (scene != _visitScene) {
            return;
        }

        var now = Time.unscaledTime;
        var theirs = new Dictionary<uint, uint>();
        var missingHere = new List<uint>();
        var differing = new List<Entry>();
        var bucketCount = reader.ReadByte();
        for (var b = 0; b < bucketCount; b++) {
            var bucket = reader.ReadByte();
            theirs.Clear();
            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++) {
                var keyHash = reader.ReadUInt32();
                theirs[keyHash] = reader.ReadUInt32();
            }

            foreach (var entry in _entries.Values) {
                if (entry.Bucket != bucket) {
                    continue;
                }

                if (!theirs.TryGetValue(entry.KeyHash, out var valueHash) || valueHash != entry.ValueHash) {
                    differing.Add(entry);
                }

                theirs.Remove(entry.KeyHash);
            }

            missingHere.AddRange(theirs.Keys);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(requestId);
        writer.Write(_visitScene ?? "");
        writer.Write((ushort) (differing.Count + missingHere.Count));
        foreach (var entry in differing) {
            writer.Write((byte) 0);
            writer.Write(entry.KeyHash);
            writer.Write(entry.Key);
            writer.Write(entry.Value);
            writer.Write((ushort) Mathf.Min(now - entry.Since, ushort.MaxValue));
        }

        foreach (var keyHash in missingHere) {
            writer.Write((byte) 1);
            writer.Write(keyHash);
        }

        writer.Flush();
        Send(partnerId, CoopCheckKind.Detail, stream.ToArray());
    }

    /// <summary>
    /// Compares the values of the partner in an answer to a request with the local ones, and writes down those that
    /// differ while both games had them for a while.
    /// </summary>
    private void OnDetail(BinaryReader reader) {
        var requestId = reader.ReadUInt32();
        var scene = reader.ReadString();
        if (scene != _visitScene || !_requests.Remove(requestId, out var request)) {
            return;
        }

        var now = Time.unscaledTime;
        if (now - request.SentAt > MaxAnswerAge) {
            AskAgain(request);
            return;
        }

        var listed = new HashSet<uint>();
        var count = reader.ReadUInt16();
        for (var i = 0; i < count; i++) {
            var kind = reader.ReadByte();
            var keyHash = reader.ReadUInt32();
            listed.Add(keyHash);
            if (kind == 1) {
                // The partner doesn't have a value that the request had, one that stayed the same here for a while
                if (FindEntry(keyHash) is { } onlyHere && now - onlyHere.Since >= StableTime) {
                    _visitOnlyHere.Add(onlyHere.Key);
                }

                continue;
            }

            var key = reader.ReadString();
            var value = reader.ReadString();
            float theirStable = reader.ReadUInt16();
            if (!_entries.TryGetValue(key, out var entry)) {
                if (theirStable >= StableTime) {
                    _visitOnlyThere.Add(key);
                }

                continue;
            }

            if (string.Equals(entry.Value, value, StringComparison.Ordinal)) {
                WriteSettled(key, now);
                continue;
            }

            if (now - entry.Since >= StableTime && theirStable >= StableTime) {
                OnDifference(entry, value, theirStable, now);
            }
        }

        // The answer leaves out the values that are the same in both games, so a difference that was written down is
        // gone if the request had the value as it is here and the answer left it out
        List<string>? settled = null;
        foreach (var pair in _open) {
            if (_entries.TryGetValue(pair.Key, out var entry) && !listed.Contains(entry.KeyHash) &&
                request.Sent.TryGetValue(entry.KeyHash, out var sentHash) && sentHash == entry.ValueHash) {
                (settled ??= []).Add(pair.Key);
            }
        }

        if (settled != null) {
            foreach (var key in settled) {
                WriteSettled(key, now);
            }
        }
    }

    /// <summary>
    /// Handles a value that differs between the games while both had it for a while: one that may be expected is kept
    /// for the end of the comparison, and any other is written down at once, once for each value in each room.
    /// </summary>
    private void OnDifference(Entry entry, string there, float thereFor, float now) {
        var reason = GetExpectedReason(entry) ??
                     (entry.Value.StartsWith(OffPrefix, StringComparison.Ordinal) &&
                      there.StartsWith(OffPrefix, StringComparison.Ordinal)
                         ? "switched off in both games"
                         : null);
        if (reason != null) {
            _visitExpected[entry.Key] = new Difference(entry.Value, there, reason, now);
            return;
        }

        if (_open.TryGetValue(entry.Key, out var open) && open.Here == entry.Value && open.There == there) {
            return;
        }

        // The story is the same in every room, so a difference of it is written down once
        if (entry.Key.StartsWith(StoryPrefix, StringComparison.Ordinal) &&
            !_writtenStoryDifferences.Add(entry.Key + "\n" + entry.Value + "\n" + there)) {
            return;
        }

        _open[entry.Key] = new Difference(entry.Value, there, null, now);

        // A value that keeps differing in new ways, like a platform that moves on its own time in each game, is written
        // down in full once, and only counted after that
        _visitDiffered.TryGetValue(entry.Key, out var times);
        _visitDiffered[entry.Key] = times + 1;
        if (times > 0) {
            return;
        }

        _visitDifferences++;

        _builder.Clear();
        _builder.Append("[State check] Differs in ").Append(_visitScene).Append(": ").Append(entry.Key);
        _builder.AppendLine().Append("    this game:    \"").Append(entry.Value).Append("\" for ")
            .Append(Seconds(now - entry.Since));
        if (entry.Previous.Length > 0) {
            _builder.Append(", before that \"").Append(entry.Previous).Append('"');
        }

        _builder.AppendLine().Append("    partner game: \"").Append(there).Append("\" for ").Append(Seconds(thereFor));
        if (entry.Scene == null && entry.Path != null) {
            _builder.AppendLine().Append("    object: ").Append(entry.Path);
        }

        _builder.AppendLine().Append("    this game runs the room: ").Append(_entityManager.IsSceneHost ? "yes" : "no");

        var hero = HeroController.SilentInstance;
        if (hero != null) {
            _builder.Append("; this player at ").Append(Where(hero.transform.position));
        }

        if (_getPartnerId() is { } partnerId && _playerData.TryGetValue(partnerId, out var partner) &&
            partner.PlayerObject != null) {
            _builder.Append(", partner shown at ").Append(Where(partner.PlayerObject.transform.position));
        }

        if (entry.Scene != null && entry.Path != null) {
            var traffic = CoopHits.GetTraffic(entry.Scene, entry.Path, now - TrafficTime);
            if (traffic.Count == 0) {
                _builder.AppendLine().Append("    no hits or touches went between the games for it in the last ")
                    .Append(Seconds(TrafficTime));
            } else {
                _builder.AppendLine().Append("    hits and touches for it:");
                foreach (var line in traffic) {
                    _builder.AppendLine().Append("      ").Append(line);
                }
            }
        }

        Logger.Info(_builder.ToString());
    }

    /// <summary>
    /// Writes down that a difference that was written down is gone, unless the value differed more than once in the
    /// room, which the end of the comparison counts instead.
    /// </summary>
    private void WriteSettled(string key, float now) {
        if (!_open.Remove(key, out var open) || _visitDiffered.TryGetValue(key, out var times) && times > 1) {
            return;
        }

        var value = _entries.TryGetValue(key, out var entry) ? entry.Value : "?";
        Logger.Info(
            $"[State check] Same again in {_visitScene} after {Seconds(now - open.Since)}: {key} = \"{value}\""
        );
    }

    /// <summary>
    /// The local values in a group that stayed the same for a while.
    /// </summary>
    private List<Entry> GetStableEntries(int bucket, float now) {
        var entries = new List<Entry>();
        foreach (var entry in _entries.Values) {
            if (entry.Bucket == bucket && now - entry.Since >= StableTime) {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// Finds the local value whose name has the given hash.
    /// </summary>
    private Entry? FindEntry(uint keyHash) {
        foreach (var entry in _entries.Values) {
            if (entry.KeyHash == keyHash) {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Sends a comparison to the partner.
    /// </summary>
    private void Send(ushort partnerId, CoopCheckKind kind, byte[] data) {
        if (data.Length > ushort.MaxValue) {
            Fail($"A comparison of the room was too long to send ({data.Length} bytes)");
            return;
        }

        _netClient.UpdateManager.SetCoopCheckUpdate(new CoopCheckUpdate {
            TargetId = partnerId,
            Kind = kind,
            Data = data
        });
    }

    #endregion

    /// <summary>
    /// A number of seconds as text.
    /// </summary>
    private static string Seconds(float seconds) {
        return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// A place in the room as text.
    /// </summary>
    private static string Where(Vector3 position) {
        return $"({position.x.ToString("0.0", CultureInfo.InvariantCulture)}, " +
               $"{position.y.ToString("0.0", CultureInfo.InvariantCulture)})";
    }

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
    /// Mixes the hashes of a name and its value into one, so that the sum of a group can add them up in any order.
    /// </summary>
    private static uint Mix(uint keyHash, uint valueHash) {
        var hash = keyHash * 0x9E3779B1u ^ valueHash;
        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35u;
        hash ^= hash >> 16;
        return hash;
    }

    /// <summary>
    /// Folds the sum of a group into the half that the digest carries, which keeps the digest small enough to go
    /// along with the other updates of a packet.
    /// </summary>
    private static ushort Fold(uint sum) {
        return (ushort) (sum ^ (sum >> 16));
    }

    /// <summary>
    /// A value of the room: its name, what it is now and since when.
    /// </summary>
    private sealed class Entry {
        /// <summary>
        /// The name of the value, which is the same in both games.
        /// </summary>
        public readonly string Key;

        /// <summary>
        /// The hash of the name.
        /// </summary>
        public readonly uint KeyHash;

        /// <summary>
        /// The group of the value in the digest.
        /// </summary>
        public readonly int Bucket;

        /// <summary>
        /// The scene and the path of the object that the value belongs to, for the hits and touches that went between
        /// the games for it. For a creature only the path, and for a story flag neither.
        /// </summary>
        public readonly string? Scene, Path;

        /// <summary>
        /// For the state of a state machine, the state machine.
        /// </summary>
        public PlayMakerFSM? Fsm;

        /// <summary>
        /// Why the value may differ between the games without anything being wrong, or null if it should not, once
        /// <see cref="ExpectedKnown"/> (see <see cref="GetExpectedReason(Entry)"/>).
        /// </summary>
        public string? Expected;

        /// <summary>
        /// Whether <see cref="Expected"/> was worked out.
        /// </summary>
        public bool ExpectedKnown;

        /// <summary>
        /// The value now and the one before it.
        /// </summary>
        public string Value = "", Previous = "";

        /// <summary>
        /// The hash of the value now.
        /// </summary>
        public uint ValueHash;

        /// <summary>
        /// When the value became what it is now, in unscaled time.
        /// </summary>
        public float Since;

        /// <summary>
        /// The reading of the room that last read the value.
        /// </summary>
        public int Sample;

        /// <summary>
        /// For a story flag, the number that it was last read as.
        /// </summary>
        public int Number;

        public Entry(string key, string? scene, string? path) {
            Key = key;
            KeyHash = Hash(key);
            Bucket = (int) (KeyHash % BucketCount);
            Scene = scene;
            Path = path;
        }
    }

    /// <summary>
    /// A request for the values of some groups: when it was sent, the groups, and the values that it had of them by
    /// the hashes of their names.
    /// </summary>
    private sealed class Request {
        /// <summary>
        /// When the request was sent, in unscaled time.
        /// </summary>
        public readonly float SentAt;

        /// <summary>
        /// The groups that the request asks about.
        /// </summary>
        public readonly List<int> Buckets;

        /// <summary>
        /// The hashes of the values that the request had, by the hashes of their names.
        /// </summary>
        public readonly Dictionary<uint, uint> Sent = new();

        public Request(float sentAt, List<int> buckets) {
            SentAt = sentAt;
            Buckets = buckets;
        }
    }

    /// <summary>
    /// A difference between the games: the value here, the value in the game of the partner, why it may be expected
    /// if it may, and when it was found.
    /// </summary>
    private readonly record struct Difference(string Here, string There, string? Reason, float Since);

    /// <summary>
    /// A field that holds the state of an object that takes hits, with the name it is shown by.
    /// </summary>
    private readonly record struct StateField(FieldInfo Field, string Name);
}
