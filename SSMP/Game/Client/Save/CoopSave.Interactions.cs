using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.Events;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Mechanisms that players use with the interact button in a checked two-player save: toll machines, doors that take an
/// item and the like (see <see cref="CoopMechanism"/>), and item receptacles. The player who uses one plays it as usual.
/// Once they paid or confirmed, the game of the partner plays the same change of the world on its copy, without the
/// prompt, the payment or what the hero does, and a partner in another room gets what it saved. A partner whose hero
/// uses the same mechanism at that moment gets the change once their hero is done with it. When both players paid, the
/// one who paid after the use of the other had arrived gets the payment back, or one of them when both uses crossed.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a replay of a mechanism may take, in seconds, before the actions that it skipped are turned back on,
    /// unless it waits for the local hero to go through the mechanism.
    /// </summary>
    private const float MaxReplayTime = 60f;

    /// <summary>
    /// The pause of an item receptacle between its effects and the rest of its unlock, which its own sequence spends on
    /// the animation of the hero.
    /// </summary>
    private const float ReceptacleEffectPause = 0.5f;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for a use of a mechanism that the sender made before the use
    /// of the other player arrived.
    /// </summary>
    private const ushort InteractionFirst = 0;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for a use of a mechanism that the sender made after the use of
    /// the other player had arrived, for which the sender gets their payment back.
    /// </summary>
    private const ushort InteractionAfterPartner = 1;

    /// <summary>
    /// Events that close a prompt that waits for an answer, in the order they are tried. The game itself closes a toll
    /// prompt with the first one when the hero gets hurt.
    /// </summary>
    private static readonly string[] PromptCancelEvents = ["HERO DAMAGED", "NO", "FALSE"];

    /// <summary>
    /// The fields of FSM actions that hold the object an action works on, by type of action.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> ActionTargetFields = new();

    /// <summary>
    /// The field of FSM actions that run a sub-FSM which holds that FSM, by type of action, or null.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo?> RunFsmFields = new();

    /// <summary>
    /// The fields of the player data by name, or null for names without a field.
    /// </summary>
    private static readonly Dictionary<string, FieldInfo?> PlayerDataFields = new(StringComparer.Ordinal);

    private static readonly FieldInfo? ReceptacleActivatedField = typeof(ItemReceptacle).GetField("isActivated", InstanceFlags);
    private static readonly FieldInfo? ReceptacleItemField = typeof(ItemReceptacle).GetField("requiredItem", InstanceFlags);
    private static readonly FieldInfo? ReceptacleItemCountField = typeof(ItemReceptacle).GetField("requiredItemCount", InstanceFlags);
    private static readonly FieldInfo? ReceptacleFlagField = typeof(ItemReceptacle).GetField("playerDataBool", InstanceFlags);
    private static readonly FieldInfo? ReceptaclePersistentField = typeof(ItemReceptacle).GetField("persistent", InstanceFlags);
    private static readonly FieldInfo? ReceptacleEffectPrefabField = typeof(ItemReceptacle).GetField("unlockEffectPrefab", InstanceFlags);
    private static readonly FieldInfo? ReceptacleEffectPointField = typeof(ItemReceptacle).GetField("unlockEffectPoint", InstanceFlags);
    private static readonly FieldInfo? ReceptacleSoundField = typeof(ItemReceptacle).GetField("unlockSound", InstanceFlags);
    private static readonly FieldInfo? ReceptacleUnlockEffectEvent = typeof(ItemReceptacle).GetField("onUnlockEffect", InstanceFlags);
    private static readonly FieldInfo? ReceptacleUnlockDelayField = typeof(ItemReceptacle).GetField("unlockDelay", InstanceFlags);
    private static readonly FieldInfo? ReceptaclePreUnlockEvent = typeof(ItemReceptacle).GetField("onPreUnlock", InstanceFlags);
    private static readonly FieldInfo? ReceptacleAnimatorField = typeof(ItemReceptacle).GetField("animator", InstanceFlags);
    private static readonly FieldInfo? ReceptacleUnlockAnimField = typeof(ItemReceptacle).GetField("_unlockAnim", StaticFlags);
    private static readonly FieldInfo? ReceptacleEventDelayField = typeof(ItemReceptacle).GetField("unlockEventDelay", InstanceFlags);
    private static readonly FieldInfo? ReceptacleUnlockEvent = typeof(ItemReceptacle).GetField("onUnlock", InstanceFlags);
    private static readonly FieldInfo? ReceptacleUnlockField = typeof(ItemReceptacle).GetField("unlock", InstanceFlags);
    private static readonly FieldInfo? ReceptacleUnlockedField = typeof(ItemReceptacle).GetField("Unlocked", InstanceFlags);

    /// <summary>
    /// The hooks for mechanisms, which stay for as long as the game runs.
    /// </summary>
    private readonly List<Hook> _interactionHooks = [];

    /// <summary>
    /// Every FSM that changed state since the scene loaded, with its mechanism or null if it isn't one.
    /// </summary>
    private readonly Dictionary<Fsm, CoopMechanism?> _mechanisms = new();

    /// <summary>
    /// The mechanisms that replay a use of the partner, with the actions they skip.
    /// </summary>
    private readonly Dictionary<Fsm, MechanismReplay> _replays = new();

    /// <summary>
    /// Mechanisms that the partner used while the local copy couldn't replay it, like while the local hero was using
    /// them, with the state that their change of the world starts in. They replay once the local copy can, unless the
    /// local player uses them too.
    /// </summary>
    private readonly Dictionary<Fsm, string> _pendingReplays = new();

    /// <summary>
    /// Mechanisms that the local player used whose change of the world still runs, with its states, whose flags of the
    /// player data go to the partner.
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _capturedMechanisms = new();

    /// <summary>
    /// The uses of mechanisms and item receptacles by the local player since the scene loaded.
    /// </summary>
    private readonly Dictionary<object, LocalInteraction> _localInteractions = new();

    /// <summary>
    /// The uses of mechanisms and item receptacles by the partner since the scene loaded, with their keys.
    /// </summary>
    private readonly Dictionary<object, ulong> _remoteInteractions = new();

    /// <summary>
    /// Item receptacles that the partner used while the local hero was using them, which play the unlock once the local
    /// hero is done with them.
    /// </summary>
    private readonly HashSet<ItemReceptacle> _pendingReceptacles = [];

    /// <summary>
    /// Flags of the player data that mechanisms of the local player set, which go to the partner. They wait while the
    /// partner is away, and are dropped when the session ends.
    /// </summary>
    private readonly Dictionary<string, int> _interactionFlags = new(StringComparer.Ordinal);

    /// <summary>
    /// The use of a mechanism whose prompt the local hero has open, or null.
    /// </summary>
    private LocalInteraction? _promptInteraction;

    /// <summary>
    /// The mechanism of the local player whose state change runs right now, whose writes of the player data are
    /// recorded, or null.
    /// </summary>
    private Fsm? _captureFsm;

    /// <summary>
    /// Whether syncing a mechanism threw, which is only logged once.
    /// </summary>
    private bool _interactionFailed;

    /// <summary>
    /// A use of a mechanism or item receptacle by the local player, from its prompt on, with what they paid for it.
    /// </summary>
    private sealed class LocalInteraction {
        public LocalInteraction(object target) {
            Target = target;
            var bytes = new byte[8];
            Random.NextBytes(bytes);
            Key = BitConverter.ToUInt64(bytes, 0);
        }

        /// <summary>
        /// The FSM of the mechanism, or the item receptacle.
        /// </summary>
        public object Target { get; }

        /// <summary>
        /// A random key: when both players paid before the use of the other arrived, the one whose use has the larger
        /// key gets their payment back.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// Whether the payment was given back already.
        /// </summary>
        public bool Refunded { get; set; }

        /// <summary>
        /// The money that the local player paid.
        /// </summary>
        public List<(CurrencyType Type, int Amount)> Currency { get; } = [];

        /// <summary>
        /// The items that the local player gave.
        /// </summary>
        public List<(CollectableItem Item, int Amount)> Items { get; } = [];

        /// <summary>
        /// The flags of the player data that the mechanism set during its prompt, which only go to the partner once
        /// the change of the world starts.
        /// </summary>
        public Dictionary<string, int> Flags { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A replay of the partner's use of a mechanism on the local copy.
    /// </summary>
    private sealed class MechanismReplay {
        public MechanismReplay(
            HashSet<string> states,
            List<FsmStateAction> skipped,
            List<HutongGames.PlayMaker.Actions.CheckTrackTriggerCount> presence
        ) {
            States = states;
            Skipped = skipped;
            Presence = presence;
        }

        /// <summary>
        /// The states of the change of the world, after which the replay ends.
        /// </summary>
        public HashSet<string> States { get; }

        /// <summary>
        /// The actions of those states that belong to the hero of the partner or check who is inside the mechanism,
        /// which are turned off during the replay.
        /// </summary>
        public List<FsmStateAction> Skipped { get; }

        /// <summary>
        /// The actions of those states that check who is inside the mechanism, which the replay stands in for.
        /// </summary>
        public List<HutongGames.PlayMaker.Actions.CheckTrackTriggerCount> Presence { get; }

        /// <summary>
        /// Whether something, like the local hero, was inside the mechanism while its check waited.
        /// </summary>
        public bool WasEntered { get; set; }

        /// <summary>
        /// When the replay started or last waited in a check of who is inside the mechanism, from which it times out.
        /// </summary>
        public float TimeoutStart { get; set; } = Time.unscaledTime;
    }

    /// <summary>
    /// Registers the hooks that find, send and replay uses of mechanisms.
    /// </summary>
    private void RegisterInteractionHooks() {
        AddInteractionHook(
            typeof(Fsm).GetMethod("SwitchState", InstanceFlags, null, [typeof(FsmState)], null),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnInteractionSwitchState)
        );

        AddInteractionHook(
            typeof(PlayerData).GetMethod("SetBool", InstanceFlags, null, [typeof(string), typeof(bool)], null),
            new Action<Action<PlayerData, string, bool>, PlayerData, string, bool>((orig, self, name, value) => {
                orig(self, name, value);
                RecordInteractionFlag(name);
            })
        );
        foreach (var methodName in (string[]) ["SetInt", "IntAdd"]) {
            var changesCounter = methodName == "IntAdd";
            AddInteractionHook(
                typeof(PlayerData).GetMethod(methodName, InstanceFlags, null, [typeof(string), typeof(int)], null),
                new Action<Action<PlayerData, string, int>, PlayerData, string, int>((orig, self, name, value) => {
                    var write = BeginPlayerDataWrite(self, name, changesCounter);
                    try {
                        orig(self, name, value);
                    } finally {
                        _playerDataWriteDepth--;
                    }

                    RecordInteractionFlag(name, write);
                })
            );
        }

        foreach (var methodName in (string[]) ["IncrementInt", "DecrementInt"]) {
            AddInteractionHook(
                typeof(PlayerData).GetMethod(methodName, InstanceFlags, null, [typeof(string)], null),
                new Action<Action<PlayerData, string>, PlayerData, string>((orig, self, name) => {
                    var write = BeginPlayerDataWrite(self, name, true);
                    try {
                        orig(self, name);
                    } finally {
                        _playerDataWriteDepth--;
                    }

                    RecordInteractionFlag(name, write);
                })
            );
        }

        // Setting a variable of the player data from an FSM doesn't go through the setters above
        AddInteractionHook(
            typeof(HutongGames.PlayMaker.Actions.SetPlayerDataVariable).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SetPlayerDataVariable>,
                HutongGames.PlayMaker.Actions.SetPlayerDataVariable>((orig, self) => {
                var name = self.VariableName != null && !self.VariableName.IsNone ? self.VariableName.Value : null;
                var write = BeginPlayerDataWrite(PlayerData.instance, name, false);
                try {
                    orig(self);
                } finally {
                    _playerDataWriteDepth--;
                }

                if (name != null) {
                    RecordInteractionFlag(name, write);
                }
            })
        );

        // TakeCurrency is short enough to be inlined into its callers, so payments are found where the amount changes
        AddInteractionHook(
            typeof(CurrencyManager).GetMethod(
                "ChangeCurrency", StaticFlags, null, [typeof(int), typeof(CurrencyType), typeof(bool)], null
            ),
            new Action<Action<int, CurrencyType, bool>, int, CurrencyType, bool>(OnInteractionChangeCurrency)
        );
        AddInteractionHook(
            typeof(CollectableItem).GetMethod("Take", InstanceFlags, null, [typeof(int), typeof(bool)], null),
            new Action<Action<CollectableItem, int, bool>, CollectableItem, int, bool>(OnInteractionTakeItem)
        );

        AddInteractionHook(
            typeof(ItemReceptacle).GetMethod("AcceptedPrompt", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ItemReceptacle>, ItemReceptacle>(OnReceptacleAccepted)
        );
        AddInteractionHook(
            typeof(ItemReceptacle).GetMethod("CanceledPrompt", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ItemReceptacle>, ItemReceptacle>(OnReceptacleCanceled)
        );
    }

    /// <summary>
    /// Creates a hook for mechanisms, logging instead of throwing when the method is missing.
    /// </summary>
    private void AddInteractionHook(MethodInfo? method, Delegate detour) {
        if (CreateHook(method, detour) is { } hook) {
            _interactionHooks.Add(hook);
        }
    }

    /// <summary>
    /// Sends the flags that mechanisms of the local player set, starts the replays that waited for the local copy, and
    /// ends replays that never got back to their idle state.
    /// </summary>
    private void UpdateInteractions(ClientPlayerData partner) {
        try {
            if (_interactionFlags.Count > 0) {
                var update = new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.WorldChange };
                foreach (var name in _interactionFlags.Keys) {
                    // The value that the save has now, since a check may have changed the flag after it was set
                    if (ReadPlayerDataFlag(name) is { } value) {
                        update.FlagNames.Add(name);
                        update.FlagValues.Add(value);
                    }
                }

                _interactionFlags.Clear();
                if (update.FlagNames.Count > 0) {
                    Send(update);
                    Logger.Info($"Sent {update.FlagNames.Count} flags of used mechanisms to {partner.Username}");
                }
            }

            if (_replays.Count > 0) {
                foreach (var pair in _replays.ToList()) {
                    if (UpdateReplayPresence(pair.Key, pair.Value)) {
                        pair.Value.TimeoutStart = Time.unscaledTime;
                    } else if (Time.unscaledTime - pair.Value.TimeoutStart > MaxReplayTime) {
                        EndReplay(pair.Key, pair.Value);
                    }
                }
            }

            if (_capturedMechanisms.Count > 0) {
                foreach (var fsm in _capturedMechanisms.Keys.Where(fsm => fsm.Owner == null).ToList()) {
                    _capturedMechanisms.Remove(fsm);
                }
            }

            StartPendingReplays();
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Forgets the mechanisms of the scene and ends their replays, for a new scene. A change of the world that the local
    /// player started stays recorded for as long as it still runs.
    /// </summary>
    private void ResetInteractions() {
        foreach (var pair in _replays.ToList()) {
            EndReplay(pair.Key, pair.Value);
        }

        _mechanisms.Clear();
        _pendingReplays.Clear();
        _localInteractions.Clear();
        _remoteInteractions.Clear();
        _pendingReceptacles.Clear();
        DropPromptInteraction();
    }

    /// <summary>
    /// Forgets the mechanisms, the changes of the world that still run and the flags that weren't sent yet, for a new
    /// session.
    /// </summary>
    private void ResetInteractionSession() {
        ResetInteractions();
        _capturedMechanisms.Clear();
        _interactionFlags.Clear();
    }

    /// <summary>
    /// Hook for <see cref="Fsm"/>.SwitchState, which follows the local uses of mechanisms and ends replays.
    /// </summary>
    private void OnInteractionSwitchState(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        if (toState == null || (!_everChecked && _replays.Count == 0)) {
            orig(self, toState!);
            return;
        }

        CoopMechanism? mechanism = null;
        try {
            mechanism = GetMechanism(self);
            if (mechanism != null) {
                BeforeMechanismSwitch(self, mechanism, toState);
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }

        var previousCapture = _captureFsm;
        _captureFsm = GetCaptureFsm(self, mechanism);
        try {
            orig(self, toState);
        } finally {
            _captureFsm = previousCapture;
        }

        if (mechanism == null) {
            return;
        }

        try {
            AfterMechanismSwitch(self, mechanism, toState);
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Gets the mechanism of an FSM, finding it the first time the FSM changes state.
    /// </summary>
    private CoopMechanism? GetMechanism(Fsm fsm) {
        if (!_mechanisms.TryGetValue(fsm, out var mechanism)) {
            mechanism = CoopMechanism.Analyze(fsm);
            _mechanisms[fsm] = mechanism;
        }

        return mechanism;
    }

    /// <summary>
    /// Follows the local hero into the prompt of a mechanism and out of it.
    /// </summary>
    private void BeforeMechanismSwitch(Fsm fsm, CoopMechanism mechanism, FsmState toState) {
        if (_replays.ContainsKey(fsm)) {
            return;
        }

        var from = fsm.ActiveStateName ?? "";
        if (mechanism.IdleStates.Contains(from) && mechanism.PromptStates.Contains(toState.Name)) {
            DropPromptInteraction();
            _promptInteraction = new LocalInteraction(fsm);
        } else if (_promptInteraction?.Target == fsm && !mechanism.PromptStates.Contains(toState.Name) &&
                   !mechanism.WorldStarts.ContainsKey(toState.Name)) {
            // The hero left the prompt without paying
            DropPromptInteraction();
        }
    }

    /// <summary>
    /// Forgets the prompt of a mechanism that the local hero left without using it. The flags that the prompt set still
    /// go to the partner, with the next changes of the world.
    /// </summary>
    private void DropPromptInteraction() {
        if (_promptInteraction == null) {
            return;
        }

        foreach (var pair in _promptInteraction.Flags) {
            _interactionFlags[pair.Key] = pair.Value;
        }

        _promptInteraction = null;
    }

    /// <summary>
    /// Sends the start of a change of the world by the local player, and ends replays and the recording of flags once
    /// the change of the world is over.
    /// </summary>
    private void AfterMechanismSwitch(Fsm fsm, CoopMechanism mechanism, FsmState toState) {
        var isEnd = (toState.Transitions?.Length ?? 0) == 0;
        if (_replays.TryGetValue(fsm, out var replay)) {
            if (isEnd || !replay.States.Contains(toState.Name)) {
                EndReplay(fsm, replay);
            }

            return;
        }

        if (_promptInteraction is { } interaction && interaction.Target == fsm &&
            mechanism.WorldStarts.TryGetValue(toState.Name, out var worldStates)) {
            _promptInteraction = null;
            OnLocalMechanismUsed(fsm, toState.Name, worldStates, interaction, isEnd);
            return;
        }

        if (_capturedMechanisms.TryGetValue(fsm, out var captured) && (isEnd || !captured.Contains(toState.Name))) {
            _capturedMechanisms.Remove(fsm);
        }
    }

    /// <summary>
    /// The local player paid for or confirmed a mechanism, so its use goes to the partner, and what it set so far with
    /// it. When the use of the partner had arrived already, the mechanism was theirs, and the local payment goes back.
    /// </summary>
    private void OnLocalMechanismUsed(
        Fsm fsm,
        string start,
        HashSet<string> worldStates,
        LocalInteraction interaction,
        bool isEnd
    ) {
        _localInteractions[fsm] = interaction;
        _pendingReplays.Remove(fsm);
        if (!isEnd) {
            _capturedMechanisms[fsm] = worldStates;
        }

        var afterPartner = _remoteInteractions.ContainsKey(fsm);
        if (_checkedWith is { } partnerId && fsm.GameObject is { } gameObject) {
            var update = new CoopSaveUpdate {
                TargetId = partnerId,
                Kind = CoopSaveUpdateKind.Interaction,
                Key = interaction.Key,
                PartCount = afterPartner ? InteractionAfterPartner : InteractionFirst,
                Scene = gameObject.scene.name,
                ObjectPath = ScenePath.Get(gameObject.transform),
                FsmName = fsm.Name ?? "",
                StateName = start
            };

            // The flags that the prompt set go with the use, so the change of the world of the partner starts with them
            foreach (var pair in interaction.Flags) {
                update.FlagNames.Add(pair.Key);
                update.FlagValues.Add(pair.Value);
            }

            Send(update);
            Logger.Info($"Sent the use of mechanism '{fsm.Name}' on {gameObject.name} to the partner");
        } else {
            foreach (var pair in interaction.Flags) {
                _interactionFlags[pair.Key] = pair.Value;
            }
        }

        if (afterPartner) {
            Refund(interaction, "had already paid for it");
        }
    }

    /// <summary>
    /// The mechanism of the local player whose writes of the player data count while an FSM runs: the mechanism itself
    /// during its prompt and its change of the world, or the sub-FSM that runs its prompt.
    /// </summary>
    private Fsm? GetCaptureFsm(Fsm fsm, CoopMechanism? mechanism) {
        if (_replays.ContainsKey(fsm)) {
            return null;
        }

        // A change of the world that runs on while the scene changes counts after its mechanism was forgotten
        if (_capturedMechanisms.ContainsKey(fsm) || (mechanism != null && _promptInteraction?.Target == fsm)) {
            return fsm;
        }

        return mechanism == null && _promptInteraction?.Target is Fsm root && GetRunningSubFsms(root).Contains(fsm)
            ? root
            : null;
    }

    /// <summary>
    /// The mechanism of the local player that the FSM which runs right now belongs to, for writes of the player data
    /// after a state was entered, like in a state that runs its actions one after another.
    /// </summary>
    private Fsm? GetExecutingCaptureFsm() {
        if (_promptInteraction == null && _capturedMechanisms.Count == 0) {
            return null;
        }

        var executing = FsmExecutionStack.ExecutingFsm;
        return executing == null
            ? null
            : GetCaptureFsm(executing, _mechanisms.TryGetValue(executing, out var mechanism) ? mechanism : null);
    }

    /// <summary>
    /// Records a write of the player data by a mechanism or dialogue about wishes of the local player.
    /// </summary>
    private void RecordInteractionFlag(string name, PlayerDataWrite write = default) {
        if (!_everChecked || string.IsNullOrEmpty(name) || PlayerData.instance == null) {
            return;
        }

        RecordTalkFlag(name, write);
        var owner = _captureFsm ?? GetExecutingCaptureFsm();
        if (owner == null || BossRoomCoop.IsHeroStateName(name) || ReadPlayerDataFlag(name) is not { } value) {
            return;
        }

        var flags = _promptInteraction is { } interaction && interaction.Target == owner
            ? interaction.Flags
            : _interactionFlags;
        flags[name] = value;

        // The flag goes to the partner with the use of the mechanism. If the story flags sent it before, the mechanism
        // of the partner would look done already and not open
        RememberStoryValues([name]);
    }

    /// <summary>
    /// Reads a flag of the player data as a number: 1 or 0 for a boolean, and the number of an enum value. Null for a
    /// name that isn't such a flag.
    /// </summary>
    private static int? ReadPlayerDataFlag(string name) {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return null;
        }

        return GetPlayerDataField(name)?.GetValue(playerData) switch {
            bool flag => flag ? 1 : 0,
            int number => number,
            Enum enumValue => Convert.ToInt32(enumValue),
            _ => null
        };
    }

    /// <summary>
    /// Hook for <see cref="CurrencyManager.ChangeCurrency"/>, which remembers what the local player paid in the prompt
    /// of a mechanism or in dialogue about wishes, and the money that such dialogue gave.
    /// </summary>
    private void OnInteractionChangeCurrency(
        Action<int, CurrencyType, bool> orig,
        int amount,
        CurrencyType type,
        bool showCounter
    ) {
        orig(amount, type, showCounter);
        if (amount == 0 || _applyingPartnerTalk) {
            return;
        }

        // Money that dialogue gives, like as a reward, whichever action or call of its FSM gives it
        if (amount > 0) {
            RecordTalkGainChange(FsmExecutionStack.ExecutingFsm, CurrencyChange + "\n" + (int) type, amount);
            return;
        }

        _promptInteraction?.Currency.Add((type, -amount));
        RecordTalkTake(null, type, CurrencyChange + "\n" + (int) type, amount);
    }

    /// <summary>
    /// Hook for <see cref="CollectableItem.Take"/>, which remembers what the local player gave in the prompt of a
    /// mechanism or in key dialogue.
    /// </summary>
    private void OnInteractionTakeItem(
        Action<CollectableItem, int, bool> orig,
        CollectableItem self,
        int amount,
        bool showCounter
    ) {
        orig(self, amount, showCounter);
        if (amount <= 0 || _applyingPartnerTalk || _applyingStoryItem) {
            return;
        }

        _promptInteraction?.Items.Add((self, amount));
        RecordTalkTake(self, null, GetItemChangeKey(TakeItemChange, self), amount);
        NoticeStoryRemoval(self, amount);
    }

    /// <summary>
    /// Plays the partner's use of a mechanism or item receptacle on the local copy.
    /// </summary>
    private void OnInteraction(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
            return;
        }

        if (_checkedWith != player.Id) {
            AddInteractionDuringCheck(player, update);
            return;
        }

        try {
            var target = ScenePath.Find(update.ObjectPath, update.Scene);
            if (update.FsmName.Length == 0) {
                OnReceptacleUsed(player, update, target);
            } else if (target != null) {
                OnMechanismUsed(player, update, target);
            } else {
                ApplyInteractionFlags(update);
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Adds what a use of the partner saved while the local game still runs the check that the game of the partner
    /// finished already, which doesn't send it again. The use itself isn't replayed, and shows once the local player
    /// enters its room again.
    /// </summary>
    private void AddInteractionDuringCheck(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkPartnerId != player.Id || _checkKey == 0 || update.Sequence >> 32 != _checkKey >> 16) {
            return;
        }

        try {
            ApplyInteractionFlags(update);
            AddInteractionItems(update);

            var loadedScenes = GetLoadedSceneNames();
            var loadedItems = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < update.ItemIds.Count && i < update.ItemScenes.Count; i++) {
                if (loadedScenes.Contains(update.ItemScenes[i].ToLowerInvariant())) {
                    loadedItems.Add(GetItemKey(update.ItemScenes[i], update.ItemIds[i]));
                }
            }

            OverrideLoadedItems(loadedItems);
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Replays the partner's use of a mechanism that is loaded locally, at once if the local copy can play it, or
    /// otherwise once it can. A prompt that the local hero has open closes first if nothing was paid in it yet.
    /// </summary>
    private void OnMechanismUsed(ClientPlayerData player, CoopSaveUpdate update, GameObject target) {
        var component = target.GetComponents<PlayMakerFSM>()
            .FirstOrDefault(fsmComponent => fsmComponent != null && fsmComponent.FsmName == update.FsmName);
        var fsm = component == null ? null : component.Fsm;
        if (fsm == null || GetMechanism(fsm) is not { } mechanism ||
            !mechanism.WorldStarts.TryGetValue(update.StateName, out var states)) {
            ApplyInteractionFlags(update);
            Logger.Warn($"Could not find the mechanism '{update.FsmName}' of {player.Username} on {update.ObjectPath}");
            return;
        }

        // Whether the local save had the mechanism done already only shows before the flags of the partner are in
        var done = IsMechanismDone(fsm, mechanism);
        ApplyInteractionFlags(update);
        _remoteInteractions[fsm] = update.Key;
        if (_localInteractions.TryGetValue(fsm, out var local)) {
            // Both players used it. A player who used it after the use of the other had arrived gets the payment back
            // in their own game, so only uses that crossed on the way are settled here
            if (update.PartCount != InteractionAfterPartner) {
                RefundIfBothPaid(local, update.Key);
            }

            return;
        }

        if (_replays.ContainsKey(fsm) || _pendingReplays.ContainsKey(fsm)) {
            return;
        }

        if (done) {
            Logger.Info($"Mechanism '{fsm.Name}' that {player.Username} used was done in the local save already");
            return;
        }

        if (CanReplay(fsm, mechanism)) {
            Logger.Info($"Replaying the use of mechanism '{fsm.Name}' by {player.Username} on {target.name}");
            StartReplay(fsm, update.StateName, states);
            return;
        }

        // The replay waits for the local copy. It is queued before the prompt closes, because closing it can take the
        // mechanism back to its idle state at once
        _pendingReplays[fsm] = update.StateName;
        if (!TryClosePrompt(fsm)) {
            Logger.Info($"The use of mechanism '{fsm.Name}' by {player.Username} waits for the local copy");
        }
    }

    /// <summary>
    /// Starts the replays of mechanisms and item receptacles that waited, once the local copies can play them.
    /// </summary>
    private void StartPendingReplays() {
        if (_pendingReplays.Count > 0) {
            foreach (var pair in _pendingReplays.ToList()) {
                var fsm = pair.Key;
                if (fsm.Owner == null || GetMechanism(fsm) is not { } mechanism ||
                    !mechanism.WorldStarts.TryGetValue(pair.Value, out var states)) {
                    _pendingReplays.Remove(fsm);
                } else if (CanReplay(fsm, mechanism)) {
                    _pendingReplays.Remove(fsm);
                    Logger.Info($"Replaying the use of mechanism '{fsm.Name}' by the partner, which waited");
                    StartReplay(fsm, pair.Value, states);
                } else {
                    // A prompt whose text was still showing closes once it waits for an answer
                    TryClosePrompt(fsm);
                }
            }
        }

        if (_pendingReceptacles.Count == 0) {
            return;
        }

        // The local hero may have read the text of a receptacle without the item, which ends without a prompt
        _pendingReceptacles.RemoveWhere(receptacle => receptacle == null);
        foreach (var receptacle in _pendingReceptacles.Where(receptacle => InteractManager.BlockingInteractable != receptacle)
                     .ToList()) {
            _pendingReceptacles.Remove(receptacle);
            StartReceptacleReplay(receptacle);
        }
    }

    /// <summary>
    /// Whether the local copy of a mechanism can replay a use of the partner now: it is active, the local hero doesn't
    /// use it, and its change of the world doesn't run or ended in a state of its own.
    /// </summary>
    private static bool CanReplay(Fsm fsm, CoopMechanism mechanism) {
        var active = fsm.ActiveStateName ?? "";
        return fsm.Owner != null && fsm.Owner.isActiveAndEnabled && !IsHeroUsing(fsm, mechanism) &&
               !mechanism.WorldStarts.Values.Any(states => states.Contains(active));
    }

    /// <summary>
    /// Whether the local save has what a mechanism saves already: the saved object of the mechanism if it has one, or
    /// otherwise every flag of the player data that its prompt and its change of the world set to true. A mechanism
    /// that saves neither counts as not done.
    /// </summary>
    private static bool IsMechanismDone(Fsm fsm, CoopMechanism mechanism) {
        var playerData = PlayerData.instance;
        var sceneData = SceneData.instance;
        if (playerData == null || sceneData == null) {
            return false;
        }

        var persistent = fsm.GameObject != null ? fsm.GameObject.GetComponent<PersistentBoolItem>() : null;
        if (persistent != null) {
            GetItemSceneAndId(persistent, out var scene, out var id);
            return sceneData.PersistentBools.TryGetValue(scene, id, out var saved) && saved.Value;
        }

        var found = false;
        foreach (var state in fsm.States ?? []) {
            if (state == null || (!mechanism.PromptStates.Contains(state.Name) &&
                                  !mechanism.WorldStarts.Values.Any(states => states.Contains(state.Name)))) {
                continue;
            }

            foreach (var action in state.Actions ?? []) {
                var name = action switch {
                    HutongGames.PlayMaker.Actions.SetPlayerDataBool { Enabled: true } setBool
                        when setBool.value.Value => setBool.boolName.Value,
                    HutongGames.PlayMaker.Actions.SetPlayerDataVariable { Enabled: true } setVariable
                        when setVariable.SetValue.GetValue() is true => setVariable.VariableName.Value,
                    _ => null
                };
                if (string.IsNullOrEmpty(name) || BossRoomCoop.IsHeroStateName(name!)) {
                    continue;
                }

                if (GetPlayerDataField(name!)?.GetValue(playerData) is not true) {
                    return false;
                }

                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Closes the prompt of a mechanism that the partner used, if the local hero has it open, hasn't paid in it yet,
    /// and it waits for an answer.
    /// </summary>
    /// <returns>Whether the prompt was closed.</returns>
    private bool TryClosePrompt(Fsm fsm) {
        if (_promptInteraction is not { } interaction || interaction.Target != fsm ||
            interaction.Currency.Count > 0 || interaction.Items.Count > 0 || !CancelPrompt(fsm)) {
            return false;
        }

        var partnerName = GetPartnerName();
        Chat($"{partnerName} already paid for this, so it opens for you too.");
        Logger.Info($"Closed the prompt of mechanism '{fsm.Name}', which {partnerName} used first");
        return true;
    }

    /// <summary>
    /// Stands in for the check of a replayed mechanism that ends its change of the world once nothing is inside it,
    /// like a door that closes behind the hero who went through. Such a check only sees the local hero, who didn't
    /// use the mechanism, so the local copy runs it only once something was inside, which lets the partner follow the
    /// player who paid.
    /// </summary>
    /// <returns>Whether the replay waits in such a check.</returns>
    private static bool UpdateReplayPresence(Fsm fsm, MechanismReplay replay) {
        var actions = fsm.ActiveState?.Actions;
        if (replay.Presence.Count == 0 || actions == null) {
            return false;
        }

        foreach (var presence in replay.Presence) {
            if (Array.IndexOf(actions, presence) < 0) {
                continue;
            }

            var target = fsm.GetOwnerDefaultTarget(presence.target);
            var track = target != null ? target.GetComponent<TrackTriggerObjects>() : null;
            if (track == null) {
                return false;
            }

            if (track.InsideCount > 0) {
                replay.WasEntered = true;
            }

            // The check fires its event itself when what it waits for is true
            if (replay.WasEntered) {
                presence.OnEnter();
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the local hero uses a mechanism: its prompt runs, or it still holds the hero, like while its dialogue
    /// closes.
    /// </summary>
    private static bool IsHeroUsing(Fsm fsm, CoopMechanism mechanism) {
        if (mechanism.PromptStates.Contains(fsm.ActiveStateName ?? "")) {
            return true;
        }

        var blocking = InteractManager.BlockingInteractable;
        return blocking != null && fsm.GameObject != null && blocking.gameObject == fsm.GameObject;
    }

    /// <summary>
    /// Replays the partner's use of a mechanism on the local copy from the state that its change of the world starts in.
    /// </summary>
    private void StartReplay(Fsm fsm, string start, HashSet<string> states) {
        PrepareReplay(fsm, states);

        // The partner used it up, so the local hero mustn't start it during the change, which doesn't listen for it
        var interactable = fsm.GameObject != null ? fsm.GameObject.GetComponent<InteractableBase>() : null;
        if (interactable != null) {
            interactable.Deactivate(false);
        }

        // As in an update of the FSM, the transitions that the state fires while it starts wait until it has started
        FsmExecutionStack.PushFsm(fsm);
        try {
            fsm.SetState(start);
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
        }
    }

    /// <summary>
    /// Turns off the actions of a change of the world that belong to the hero who used the mechanism, for replaying it.
    /// </summary>
    private void PrepareReplay(Fsm fsm, HashSet<string> states) {
        if (_replays.TryGetValue(fsm, out var previous)) {
            EndReplay(fsm, previous);
        }

        var hero = HeroController.instance != null ? HeroController.instance.gameObject : null;
        var skipped = new List<FsmStateAction>();
        var presence = new List<HutongGames.PlayMaker.Actions.CheckTrackTriggerCount>();
        foreach (var state in fsm.States ?? []) {
            if (state == null || !states.Contains(state.Name)) {
                continue;
            }

            foreach (var action in state.Actions ?? []) {
                if (action == null || !action.Enabled) {
                    continue;
                }

                if (action is HutongGames.PlayMaker.Actions.CheckTrackTriggerCount check) {
                    presence.Add(check);
                } else if (!IsHeroAction(fsm, action, hero)) {
                    continue;
                }

                action.Enabled = false;
                skipped.Add(action);
            }
        }

        _replays[fsm] = new MechanismReplay(states, skipped, presence);
    }

    /// <summary>
    /// Turns the actions that a replay skipped back on.
    /// </summary>
    private void EndReplay(Fsm fsm, MechanismReplay replay) {
        foreach (var action in replay.Skipped) {
            action.Enabled = true;
        }

        _replays.Remove(fsm);
    }

    /// <summary>
    /// Whether an action of a mechanism belongs to the hero who used it: it ends their dialogue, belongs to their
    /// prompt, or works on the hero.
    /// </summary>
    private static bool IsHeroAction(Fsm fsm, FsmStateAction action, GameObject? hero) {
        if (action.GetType().Name is "EndDialogue" or "EndInteractEvents" || CoopMechanism.IsPromptAction(action)) {
            return true;
        }

        if (hero == null) {
            return false;
        }

        foreach (var field in GetActionTargetFields(action.GetType())) {
            var target = field.GetValue(action) switch {
                FsmOwnerDefault owner => fsm.GetOwnerDefaultTarget(owner),
                FsmGameObject gameObject => gameObject.Value,
                _ => null
            };
            if (target != null && (target == hero || target.transform.IsChildOf(hero.transform))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the fields of a type of FSM action that hold the object it works on.
    /// </summary>
    private static FieldInfo[] GetActionTargetFields(Type type) {
        if (!ActionTargetFields.TryGetValue(type, out var fields)) {
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(FsmOwnerDefault) || field.FieldType == typeof(FsmGameObject))
                .ToArray();
            ActionTargetFields[type] = fields;
        }

        return fields;
    }

    /// <summary>
    /// Closes the prompt that waits for an answer in a mechanism or the sub-FSM that runs its prompt, the way the game
    /// closes it when the hero gets hurt.
    /// </summary>
    /// <returns>Whether a prompt was closed.</returns>
    private static bool CancelPrompt(Fsm fsm) {
        var subFsms = GetRunningSubFsms(fsm);
        var deepest = subFsms.Count > 0 ? subFsms[subFsms.Count - 1] : fsm;
        var transitions = deepest.ActiveState?.Transitions ?? [];

        // Only a prompt that still waits for an answer can close without leaving the hero or the payment half done
        if (!transitions.Any(transition => transition.EventName is "YES" or "TRUE")) {
            return false;
        }

        foreach (var eventName in PromptCancelEvents) {
            if (transitions.Any(transition => transition.EventName == eventName)) {
                deepest.Event(eventName);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the sub-FSMs that the active state of an FSM runs, and the ones that those run, outermost first.
    /// </summary>
    private static List<Fsm> GetRunningSubFsms(Fsm fsm) {
        var subFsms = new List<Fsm>();
        var current = fsm;
        while (subFsms.Count < 4 && current.ActiveState?.Actions is { } actions) {
            Fsm? next = null;
            foreach (var action in actions) {
                if (action != null && GetRunFsmField(action.GetType())?.GetValue(action) is Fsm subFsm) {
                    next = subFsm;
                    break;
                }
            }

            if (next == null || next == fsm || subFsms.Contains(next)) {
                break;
            }

            subFsms.Add(next);
            current = next;
        }

        return subFsms;
    }

    /// <summary>
    /// Gets the field of a type of FSM action that holds the sub-FSM it runs, or null.
    /// </summary>
    private static FieldInfo? GetRunFsmField(Type type) {
        if (RunFsmFields.TryGetValue(type, out var field)) {
            return field;
        }

        for (var current = type; current != null && field == null; current = current.BaseType) {
            field = current.GetField("runFsm", InstanceFlags | BindingFlags.DeclaredOnly);
        }

        if (field != null && field.FieldType != typeof(Fsm)) {
            field = null;
        }

        RunFsmFields[type] = field;
        return field;
    }

    /// <summary>
    /// Settles two uses of the same mechanism that crossed on the way, so each player paid before the use of the other
    /// arrived. Both games give the payment back to the player whose use has the larger key, so only one player gets it.
    /// </summary>
    private void RefundIfBothPaid(LocalInteraction interaction, ulong partnerKey) {
        if (interaction.Key > partnerKey) {
            Refund(interaction, "paid for it at the same moment");
        }
    }

    /// <summary>
    /// Gives the local player back what they paid for a mechanism that the partner paid for too.
    /// </summary>
    /// <param name="interaction">The use of the local player.</param>
    /// <param name="how">What the partner did, for the message.</param>
    private void Refund(LocalInteraction interaction, string how) {
        if (interaction.Refunded || (interaction.Currency.Count == 0 && interaction.Items.Count == 0)) {
            return;
        }

        interaction.Refunded = true;
        var returned = 0;
        var shared = 0;
        foreach (var (type, amount) in interaction.Currency) {
            CurrencyManager.AddCurrency(amount, type, true);
            returned++;
        }

        foreach (var (item, amount) in interaction.Items) {
            // An item of the story whose removal is shared is taken from both players anyway, so giving it
            // back here would undo that, and which of the two arrived last would decide whether the local
            // player keeps it
            if (item == null) {
                continue;
            }

            if (IsStoryItem(item, out var story) && story.ShareRemoval) {
                shared++;
                continue;
            }

            item.AddAmount(amount);
            returned++;
        }

        if (returned == 0) {
            // What the story takes for good is gone for both players, so there is nothing to give back
            if (shared > 0) {
                Chat($"{GetPartnerName()} {how}, and what you gave is used up for both of you.");
            }

            return;
        }

        Chat($"{GetPartnerName()} {how}, so you got your payment back.");
        Logger.Info($"Gave back the payment for a mechanism that the partner {how}");
    }

    /// <summary>
    /// The name of the checked partner, for messages.
    /// </summary>
    private string GetPartnerName() {
        return _checkedWith is { } id && _playerData.TryGetValue(id, out var partner) ? partner.Username : "Your partner";
    }

    /// <summary>
    /// Sets the flags of the player data that the partner set, like with a mechanism or in the story, and takes them as
    /// known by both games.
    /// </summary>
    /// <returns>How many flags changed.</returns>
    private int ApplyInteractionFlags(CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return 0;
        }

        var changed = 0;
        var applied = new List<string>();
        for (var i = 0; i < update.FlagNames.Count && i < update.FlagValues.Count; i++) {
            var name = update.FlagNames[i];
            var value = update.FlagValues[i];
            if (BossRoomCoop.IsHeroStateName(name) || !IsNewerChange(_flagSequences, update, name)) {
                continue;
            }

            applied.Add(name);
            var field = GetPlayerDataField(name);
            if (field?.FieldType == typeof(bool)) {
                if (playerData.GetBool(name) != (value != 0)) {
                    playerData.SetBool(name, value != 0);
                    changed++;
                }
            } else if (field?.FieldType == typeof(int)) {
                if (playerData.GetInt(name) != value) {
                    playerData.SetInt(name, value);
                    changed++;
                }
            } else if (field != null && field.FieldType.IsEnum && Convert.ToInt32(field.GetValue(playerData)) != value) {
                field.SetValue(playerData, Enum.ToObject(field.FieldType, value));
                changed++;
            }
        }

        RememberStoryValues(applied);
        return changed;
    }

    /// <summary>
    /// Gets the public field of the player data with a name, or null.
    /// </summary>
    private static FieldInfo? GetPlayerDataField(string name) {
        if (!PlayerDataFields.TryGetValue(name, out var field)) {
            field = typeof(PlayerData).GetField(name, BindingFlags.Instance | BindingFlags.Public);
            PlayerDataFields[name] = field;
        }

        return field;
    }

    /// <summary>
    /// Hook for ItemReceptacle.AcceptedPrompt: the local player gives the item, so the use goes to the partner. When the
    /// use of the partner had arrived already, the local player gets the item back.
    /// </summary>
    private void OnReceptacleAccepted(Action<ItemReceptacle> orig, ItemReceptacle self) {
        orig(self);
        if (!_everChecked) {
            return;
        }

        try {
            _pendingReceptacles.Remove(self);
            var interaction = new LocalInteraction(self);
            if (ReceptacleItemField?.GetValue(self) is CollectableItem item && item != null &&
                ReceptacleItemCountField?.GetValue(self) is int count && count > 0) {
                interaction.Items.Add((item, count));
            }

            _localInteractions[self] = interaction;
            var flag = ReceptacleFlagField?.GetValue(self) as string ?? "";
            var afterPartner = _remoteInteractions.ContainsKey(self);
            if (_checkedWith is not { } partnerId) {
                if (flag.Length > 0) {
                    _interactionFlags[flag] = 1;
                }
            } else {
                var update = new CoopSaveUpdate {
                    TargetId = partnerId,
                    Kind = CoopSaveUpdateKind.Interaction,
                    Key = interaction.Key,
                    PartCount = afterPartner ? InteractionAfterPartner : InteractionFirst,
                    Scene = self.gameObject.scene.name,
                    ObjectPath = ScenePath.Get(self.transform)
                };

                // The flag goes with the use, so a partner in another room gets it together with the saved object
                if (flag.Length > 0) {
                    update.FlagNames.Add(flag);
                    update.FlagValues.Add(1);
                }

                // A saved object that resets at benches isn't added to the save of a partner in another room
                if (ReceptaclePersistentField?.GetValue(self) is PersistentBoolItem persistent && persistent != null &&
                    !persistent.GetIsSemiPersistent()) {
                    GetItemSceneAndId(persistent, out var scene, out var id);
                    update.ItemScenes.Add(scene);
                    update.ItemIds.Add(id);
                }

                Send(update);
                Logger.Info($"Sent the use of item receptacle {self.name} to the partner");
            }

            if (afterPartner) {
                Refund(interaction, "had already paid for it");
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Hook for ItemReceptacle.CanceledPrompt: an item receptacle that the partner used while the local prompt was open
    /// plays its unlock now.
    /// </summary>
    private void OnReceptacleCanceled(Action<ItemReceptacle> orig, ItemReceptacle self) {
        orig(self);
        if (!_pendingReceptacles.Remove(self)) {
            return;
        }

        try {
            StartReceptacleReplay(self);
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Plays the partner's use of an item receptacle on the local copy, or adds it to the save if its room isn't
    /// loaded.
    /// </summary>
    private void OnReceptacleUsed(ClientPlayerData player, CoopSaveUpdate update, GameObject? target) {
        ApplyInteractionFlags(update);
        var receptacle = target == null ? null : target.GetComponent<ItemReceptacle>();
        if (receptacle == null) {
            AddInteractionItems(update);
            return;
        }

        _remoteInteractions[receptacle] = update.Key;
        if (_localInteractions.TryGetValue(receptacle, out var local)) {
            if (update.PartCount != InteractionAfterPartner) {
                RefundIfBothPaid(local, update.Key);
            }

            return;
        }

        if (ReceptacleActivatedField?.GetValue(receptacle) is true) {
            return;
        }

        // The text and prompt of the receptacle hold the local hero; the unlock plays once they are done
        if (InteractManager.BlockingInteractable == receptacle) {
            _pendingReceptacles.Add(receptacle);
            Logger.Info($"The use of item receptacle {receptacle.name} by {player.Username} waits for the local hero");
            return;
        }

        StartReceptacleReplay(receptacle);
        Logger.Info($"Replaying the use of item receptacle {receptacle.name} by {player.Username}");
    }

    /// <summary>
    /// Sets the saved objects of an item receptacle that the partner used in the save, without the list of saved objects
    /// of the world, because the partner changed them with the interact button.
    /// </summary>
    private static void AddInteractionItems(CoopSaveUpdate update) {
        var sceneData = SceneData.instance;
        if (sceneData == null) {
            return;
        }

        for (var i = 0; i < update.ItemIds.Count && i < update.ItemScenes.Count; i++) {
            sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
                ID = update.ItemIds[i],
                SceneName = update.ItemScenes[i],
                Value = true,
                IsSemiPersistent = false
            });
        }
    }

    /// <summary>
    /// Starts the unlock of an item receptacle without the item and the hero.
    /// </summary>
    private static void StartReceptacleReplay(ItemReceptacle receptacle) {
        if (receptacle.isActiveAndEnabled && ReceptacleActivatedField?.GetValue(receptacle) is not true) {
            receptacle.StartCoroutine(ReplayReceptacle(receptacle));
        }
    }

    /// <summary>
    /// The unlock sequence of an item receptacle from the point where the item was given, without the parts of the
    /// hero: its effects, its delays, its animation and what it opens.
    /// </summary>
    private static IEnumerator ReplayReceptacle(ItemReceptacle receptacle) {
        ReceptacleActivatedField?.SetValue(receptacle, true);
        if (ReceptacleFlagField?.GetValue(receptacle) is string flag && flag.Length > 0 && PlayerData.instance != null) {
            PlayerData.instance.SetBool(flag, true);
        }

        receptacle.Deactivate(false);

        var effectPoint = ReceptacleEffectPointField?.GetValue(receptacle) as Transform;
        var effectPosition = (effectPoint != null ? effectPoint : receptacle.transform).position;
        if (ReceptacleEffectPrefabField?.GetValue(receptacle) is GameObject prefab && prefab != null) {
            prefab.Spawn(effectPosition);
        }

        if (ReceptacleSoundField?.GetValue(receptacle) is AudioEvent sound) {
            sound.SpawnAndPlayOneShot((AudioSource?) null, receptacle.transform.position, (Action?) null);
        }

        InvokeReceptacleEvent(ReceptacleUnlockEffectEvent, receptacle);
        yield return new WaitForSeconds(ReceptacleEffectPause);

        if (ReceptacleUnlockDelayField?.GetValue(receptacle) is float unlockDelay && unlockDelay > 0f) {
            yield return new WaitForSeconds(unlockDelay);
        }

        InvokeReceptacleEvent(ReceptaclePreUnlockEvent, receptacle);
        if (ReceptacleAnimatorField?.GetValue(receptacle) is Animator animator && animator != null &&
            ReceptacleUnlockAnimField?.GetValue(null) is int unlockAnim) {
            animator.enabled = true;
            animator.Play(unlockAnim, 0, 0f);
            yield return null;
            yield return new WaitForSeconds(animator.GetCurrentAnimatorStateInfo(0).length);
        }

        if (ReceptacleEventDelayField?.GetValue(receptacle) is float eventDelay && eventDelay > 0f) {
            yield return new WaitForSeconds(eventDelay);
        }

        InvokeReceptacleEvent(ReceptacleUnlockEvent, receptacle);
        if (ReceptacleUnlockField?.GetValue(receptacle) is UnlockablePropBase unlock && unlock != null) {
            unlock.Open();
        }

        (ReceptacleUnlockedField?.GetValue(receptacle) as Action)?.Invoke();
    }

    /// <summary>
    /// Invokes a Unity event of an item receptacle.
    /// </summary>
    private static void InvokeReceptacleEvent(FieldInfo? field, ItemReceptacle receptacle) {
        (field?.GetValue(receptacle) as UnityEvent)?.Invoke();
    }

    /// <summary>
    /// Logs the first error of syncing a mechanism.
    /// </summary>
    private void LogInteractionError(Exception e) {
        if (!_interactionFailed) {
            _interactionFailed = true;
            Logger.Error($"Could not sync a mechanism of the two-player save:\n{e}");
        }
    }
}
