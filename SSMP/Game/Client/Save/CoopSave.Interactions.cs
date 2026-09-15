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
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Mechanisms that players use with the interact button in a checked two-player save: toll machines, doors that take an
/// item and the like (see <see cref="CoopMechanism"/>), and item receptacles. The player who uses one plays it as usual.
/// Once they paid or confirmed, the game of the partner plays the same change of the world on its copy, without the
/// prompt, the payment or what the hero does, and a partner in another room gets what it saved. When both players pay
/// for the same mechanism at the same moment, one of them gets the payment back.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long after a use of a mechanism, in seconds, a use of the same mechanism by the other player counts as
    /// happening at the same moment.
    /// </summary>
    private const float InteractionConflictTime = 15f;

    /// <summary>
    /// How long a replay of a mechanism may take, in seconds, before the actions that it skipped are turned back on.
    /// </summary>
    private const float MaxReplayTime = 60f;

    /// <summary>
    /// The pause of an item receptacle between its effects and the rest of its unlock, which its own sequence spends on
    /// the animation of the hero.
    /// </summary>
    private const float ReceptacleEffectPause = 0.5f;

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

    private static readonly MethodInfo? InteractableDeactivateMethod =
        typeof(InteractableBase).GetMethod("Deactivate", InstanceFlags, null, [typeof(bool)], null);

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
    /// Mechanisms whose prompt was closed because the partner used them first, with the state that their change of the
    /// world starts in once the prompt has closed.
    /// </summary>
    private readonly Dictionary<Fsm, string> _pendingReplays = new();

    /// <summary>
    /// Mechanisms that the local player used whose change of the world still runs, with its states, whose flags of the
    /// player data go to the partner.
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _capturedMechanisms = new();

    /// <summary>
    /// The latest uses of mechanisms and item receptacles by the local player.
    /// </summary>
    private readonly Dictionary<object, LocalInteraction> _localInteractions = new();

    /// <summary>
    /// The latest uses of mechanisms and item receptacles by the partner, with when they arrived and their keys.
    /// </summary>
    private readonly Dictionary<object, (float Time, ulong Key)> _remoteInteractions = new();

    /// <summary>
    /// Item receptacles that the partner used while the local hero had their prompt open, which play the unlock once the
    /// local prompt is canceled.
    /// </summary>
    private readonly HashSet<ItemReceptacle> _pendingReceptacles = [];

    /// <summary>
    /// Flags of the player data that mechanisms of the local player set, which go to the partner.
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
        /// A random key: when both players paid at the same moment, the one whose use has the larger key gets their
        /// payment back.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// When the prompt opened, and then when the change of the world started.
        /// </summary>
        public float Started { get; set; } = Time.unscaledTime;

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
        public MechanismReplay(HashSet<string> states, List<FsmStateAction> skipped) {
            States = states;
            Skipped = skipped;
        }

        /// <summary>
        /// The states of the change of the world, after which the replay ends.
        /// </summary>
        public HashSet<string> States { get; }

        /// <summary>
        /// The actions of those states that belong to the hero of the partner, which are turned off during the replay.
        /// </summary>
        public List<FsmStateAction> Skipped { get; }

        /// <summary>
        /// When the replay started.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;
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
            AddInteractionHook(
                typeof(PlayerData).GetMethod(methodName, InstanceFlags, null, [typeof(string), typeof(int)], null),
                new Action<Action<PlayerData, string, int>, PlayerData, string, int>((orig, self, name, value) => {
                    orig(self, name, value);
                    RecordInteractionFlag(name);
                })
            );
        }

        foreach (var methodName in (string[]) ["IncrementInt", "DecrementInt"]) {
            AddInteractionHook(
                typeof(PlayerData).GetMethod(methodName, InstanceFlags, null, [typeof(string)], null),
                new Action<Action<PlayerData, string>, PlayerData, string>((orig, self, name) => {
                    orig(self, name);
                    RecordInteractionFlag(name);
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
                orig(self);
                if (self.VariableName != null && !self.VariableName.IsNone) {
                    RecordInteractionFlag(self.VariableName.Value);
                }
            })
        );

        AddInteractionHook(
            typeof(CurrencyManager).GetMethod(
                "TakeCurrency", StaticFlags, null, [typeof(int), typeof(CurrencyType), typeof(bool)], null
            ),
            new Action<Action<int, CurrencyType, bool>, int, CurrencyType, bool>(OnInteractionTakeCurrency)
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
    /// Sends the flags that mechanisms of the local player set, and ends replays that never got back to their idle
    /// state.
    /// </summary>
    private void UpdateInteractions(ClientPlayerData partner) {
        try {
            if (_interactionFlags.Count > 0) {
                var update = new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.WorldChange };
                foreach (var pair in _interactionFlags) {
                    update.FlagNames.Add(pair.Key);
                    update.FlagValues.Add(pair.Value);
                }

                _interactionFlags.Clear();
                Send(update);
                Logger.Info($"Sent {update.FlagNames.Count} flags of used mechanisms to {partner.Username}");
            }

            foreach (var pair in _replays.Where(pair => Time.unscaledTime - pair.Value.Started > MaxReplayTime).ToList()) {
                EndReplay(pair.Key, pair.Value);
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Forgets the mechanisms of the scene and ends their replays, for a new scene or session.
    /// </summary>
    private void ResetInteractions() {
        foreach (var pair in _replays.ToList()) {
            EndReplay(pair.Key, pair.Value);
        }

        _mechanisms.Clear();
        _pendingReplays.Clear();
        _capturedMechanisms.Clear();
        _localInteractions.Clear();
        _remoteInteractions.Clear();
        _pendingReceptacles.Clear();
        _promptInteraction = null;
    }

    /// <summary>
    /// Hook for <see cref="Fsm"/>.SwitchState, which follows the local uses of mechanisms, turns a prompt that was closed
    /// for the partner into the replay of their use, and ends replays.
    /// </summary>
    private void OnInteractionSwitchState(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        if (toState == null || (_checkedWith == null && _replays.Count == 0 && _pendingReplays.Count == 0)) {
            orig(self, toState!);
            return;
        }

        CoopMechanism? mechanism = null;
        try {
            mechanism = GetMechanism(self);
            if (mechanism != null) {
                toState = BeforeMechanismSwitch(self, mechanism, toState);
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
    /// Follows a mechanism into its prompt and out of it, and sends a prompt that was closed for the partner's use into
    /// the change of the world instead of its idle state.
    /// </summary>
    /// <returns>The state that the mechanism goes to.</returns>
    private FsmState BeforeMechanismSwitch(Fsm fsm, CoopMechanism mechanism, FsmState toState) {
        if (_pendingReplays.TryGetValue(fsm, out var pendingStart) && mechanism.IdleStates.Contains(toState.Name)) {
            _pendingReplays.Remove(fsm);
            if (_promptInteraction?.Target == fsm) {
                _promptInteraction = null;
            }

            if (fsm.GetState(pendingStart) is { } start &&
                mechanism.WorldStarts.TryGetValue(pendingStart, out var states)) {
                PrepareReplay(fsm, states);
                Logger.Info($"Replaying the use of mechanism '{fsm.Name}' by the partner after closing the local prompt");
                return start;
            }
        }

        if (_replays.ContainsKey(fsm)) {
            return toState;
        }

        var from = fsm.ActiveStateName ?? "";
        if (mechanism.IdleStates.Contains(from) && mechanism.PromptStates.Contains(toState.Name)) {
            _promptInteraction = new LocalInteraction(fsm);
        } else if (mechanism.IdleStates.Contains(toState.Name) && _promptInteraction?.Target == fsm) {
            // The hero left the prompt without paying
            _promptInteraction = null;
        }

        return toState;
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
    /// it.
    /// </summary>
    private void OnLocalMechanismUsed(
        Fsm fsm,
        string start,
        HashSet<string> worldStates,
        LocalInteraction interaction,
        bool isEnd
    ) {
        interaction.Started = Time.unscaledTime;
        _localInteractions[fsm] = interaction;
        foreach (var pair in interaction.Flags) {
            _interactionFlags[pair.Key] = pair.Value;
        }

        if (!isEnd) {
            _capturedMechanisms[fsm] = worldStates;
        }

        if (_checkedWith is { } partnerId && fsm.GameObject is { } gameObject) {
            Send(new CoopSaveUpdate {
                TargetId = partnerId,
                Kind = CoopSaveUpdateKind.Interaction,
                Key = interaction.Key,
                Scene = gameObject.scene.name,
                ObjectPath = ScenePath.Get(gameObject.transform),
                FsmName = fsm.Name ?? "",
                StateName = start
            });
            Logger.Info($"Sent the use of mechanism '{fsm.Name}' on {gameObject.name} to the partner");
        }

        if (_remoteInteractions.TryGetValue(fsm, out var remote) &&
            Time.unscaledTime - remote.Time < InteractionConflictTime) {
            RefundIfBothPaid(interaction, remote.Key);
        }
    }

    /// <summary>
    /// The mechanism of the local player whose writes of the player data count while an FSM changes state: the
    /// mechanism itself during its prompt and its change of the world, or the sub-FSM that runs its prompt.
    /// </summary>
    private Fsm? GetCaptureFsm(Fsm fsm, CoopMechanism? mechanism) {
        if (mechanism != null) {
            return !_replays.ContainsKey(fsm) &&
                   (_promptInteraction?.Target == fsm || _capturedMechanisms.ContainsKey(fsm))
                ? fsm
                : null;
        }

        return _promptInteraction?.Target is Fsm root && GetRunningSubFsms(root).Contains(fsm) ? root : null;
    }

    /// <summary>
    /// Records a write of the player data by a mechanism of the local player.
    /// </summary>
    private void RecordInteractionFlag(string name) {
        if (_captureFsm == null || string.IsNullOrEmpty(name) || BossRoomCoop.IsHeroStateName(name) ||
            PlayerData.instance == null) {
            return;
        }

        int value;
        switch (GetPlayerDataField(name)?.GetValue(PlayerData.instance)) {
            case bool flag:
                value = flag ? 1 : 0;
                break;
            case int number:
                value = number;
                break;
            default:
                return;
        }

        var flags = _promptInteraction is { } interaction && interaction.Target == _captureFsm
            ? interaction.Flags
            : _interactionFlags;
        flags[name] = value;
    }

    /// <summary>
    /// Hook for <see cref="CurrencyManager.TakeCurrency"/>, which remembers what the local player paid in the prompt of
    /// a mechanism.
    /// </summary>
    private void OnInteractionTakeCurrency(
        Action<int, CurrencyType, bool> orig,
        int amount,
        CurrencyType type,
        bool showCounter
    ) {
        orig(amount, type, showCounter);
        if (_promptInteraction != null && amount > 0) {
            _promptInteraction.Currency.Add((type, amount));
        }
    }

    /// <summary>
    /// Hook for <see cref="CollectableItem.Take"/>, which remembers what the local player gave in the prompt of a
    /// mechanism.
    /// </summary>
    private void OnInteractionTakeItem(
        Action<CollectableItem, int, bool> orig,
        CollectableItem self,
        int amount,
        bool showCounter
    ) {
        orig(self, amount, showCounter);
        if (_promptInteraction != null && amount > 0) {
            _promptInteraction.Items.Add((self, amount));
        }
    }

    /// <summary>
    /// Plays the partner's use of a mechanism or item receptacle on the local copy.
    /// </summary>
    private void OnInteraction(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
            return;
        }

        try {
            var target = ScenePath.Find(update.ObjectPath, update.Scene);
            if (update.FsmName.Length == 0) {
                OnReceptacleUsed(player, update, target);
            } else if (target != null) {
                OnMechanismUsed(player, update, target);
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Replays the partner's use of a mechanism that is loaded locally. An idle copy plays the change of the world at
    /// once. A copy whose prompt the local hero has open closes the prompt first if nothing was paid yet.
    /// </summary>
    private void OnMechanismUsed(ClientPlayerData player, CoopSaveUpdate update, GameObject target) {
        var component = target.GetComponents<PlayMakerFSM>()
            .FirstOrDefault(fsmComponent => fsmComponent != null && fsmComponent.FsmName == update.FsmName);
        var fsm = component == null ? null : component.Fsm;
        if (fsm == null || GetMechanism(fsm) is not { } mechanism ||
            !mechanism.WorldStarts.TryGetValue(update.StateName, out var states)) {
            Logger.Warn($"Could not find the mechanism '{update.FsmName}' of {player.Username} on {update.ObjectPath}");
            return;
        }

        var now = Time.unscaledTime;
        _remoteInteractions[fsm] = (now, update.Key);
        if (_localInteractions.TryGetValue(fsm, out var local) && now - local.Started < InteractionConflictTime) {
            RefundIfBothPaid(local, update.Key);
            return;
        }

        var active = fsm.ActiveStateName ?? "";
        if (mechanism.IdleStates.Contains(active)) {
            if (!component!.isActiveAndEnabled) {
                return;
            }

            PrepareReplay(fsm, states);
            Logger.Info($"Replaying the use of mechanism '{fsm.Name}' by {player.Username} on {target.name}");
            fsm.SetState(update.StateName);
            return;
        }

        // Once the local player paid, both paid, and the local change of the world settles who gets it back
        if (mechanism.PromptStates.Contains(active) && _promptInteraction is { } interaction &&
            interaction.Target == fsm && interaction.Currency.Count == 0 && interaction.Items.Count == 0 &&
            CancelPrompt(fsm)) {
            _pendingReplays[fsm] = update.StateName;
            Logger.Info($"Closed the prompt of mechanism '{fsm.Name}', which {player.Username} used first");
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
        foreach (var state in fsm.States ?? []) {
            if (state == null || !states.Contains(state.Name)) {
                continue;
            }

            foreach (var action in state.Actions ?? []) {
                if (action != null && action.Enabled && IsHeroAction(fsm, action, hero)) {
                    action.Enabled = false;
                    skipped.Add(action);
                }
            }
        }

        _replays[fsm] = new MechanismReplay(states, skipped);
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
    /// Gives the local player back what they paid when the partner paid for the same mechanism at the same moment.
    /// Both games decide it the same way, so only one player gets their payment back.
    /// </summary>
    private void RefundIfBothPaid(LocalInteraction interaction, ulong partnerKey) {
        if (interaction.Refunded || interaction.Key <= partnerKey ||
            (interaction.Currency.Count == 0 && interaction.Items.Count == 0)) {
            return;
        }

        interaction.Refunded = true;
        foreach (var (type, amount) in interaction.Currency) {
            CurrencyManager.AddCurrency(amount, type, true);
        }

        foreach (var (item, amount) in interaction.Items) {
            if (item != null) {
                item.AddAmount(amount);
            }
        }

        var partnerName = _checkedWith is { } id && _playerData.TryGetValue(id, out var partner)
            ? partner.Username
            : "Your partner";
        Chat($"{partnerName} paid for it at the same moment, so you got your payment back.");
        Logger.Info("Gave back the payment for a mechanism that the partner paid for at the same moment");
    }

    /// <summary>
    /// Sets the flags of the player data that a mechanism of the partner set.
    /// </summary>
    /// <returns>How many flags changed.</returns>
    private static int ApplyInteractionFlags(CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        var changed = 0;
        for (var i = 0; i < update.FlagNames.Count && i < update.FlagValues.Count; i++) {
            var name = update.FlagNames[i];
            var value = update.FlagValues[i];
            if (BossRoomCoop.IsHeroStateName(name)) {
                continue;
            }

            var field = GetPlayerDataField(name);
            if (field?.FieldType == typeof(bool)) {
                if (playerData.GetBool(name) != (value != 0)) {
                    playerData.SetBool(name, value != 0);
                    changed++;
                }
            } else if (field?.FieldType == typeof(int) && playerData.GetInt(name) != value) {
                playerData.SetInt(name, value);
                changed++;
            }
        }

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
    /// Hook for ItemReceptacle.AcceptedPrompt: the local player gives the item, so the use goes to the partner.
    /// </summary>
    private void OnReceptacleAccepted(Action<ItemReceptacle> orig, ItemReceptacle self) {
        orig(self);
        if (_checkedWith is not { } partnerId) {
            return;
        }

        try {
            var interaction = new LocalInteraction(self);
            if (ReceptacleItemField?.GetValue(self) is CollectableItem item && item != null &&
                ReceptacleItemCountField?.GetValue(self) is int count && count > 0) {
                interaction.Items.Add((item, count));
            }

            _localInteractions[self] = interaction;
            if (ReceptacleFlagField?.GetValue(self) is string flag && flag.Length > 0) {
                _interactionFlags[flag] = 1;
            }

            var update = new CoopSaveUpdate {
                TargetId = partnerId,
                Kind = CoopSaveUpdateKind.Interaction,
                Key = interaction.Key,
                Scene = self.gameObject.scene.name,
                ObjectPath = ScenePath.Get(self.transform)
            };
            if (ReceptaclePersistentField?.GetValue(self) is PersistentBoolItem persistent && persistent != null) {
                GetItemSceneAndId(persistent, out var scene, out var id);
                update.ItemScenes.Add(scene);
                update.ItemIds.Add(id);
            }

            Send(update);
            Logger.Info($"Sent the use of item receptacle {self.name} to the partner");

            if (_remoteInteractions.TryGetValue(self, out var remote) &&
                Time.unscaledTime - remote.Time < InteractionConflictTime) {
                RefundIfBothPaid(interaction, remote.Key);
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
        var receptacle = target == null ? null : target.GetComponent<ItemReceptacle>();
        if (receptacle == null) {
            AddInteractionItems(update);
            return;
        }

        var now = Time.unscaledTime;
        _remoteInteractions[receptacle] = (now, update.Key);
        if (_localInteractions.TryGetValue(receptacle, out var local) && now - local.Started < InteractionConflictTime) {
            RefundIfBothPaid(local, update.Key);
            return;
        }

        if (ReceptacleActivatedField?.GetValue(receptacle) is true) {
            return;
        }

        if (InteractManager.BlockingInteractable == receptacle) {
            _pendingReceptacles.Add(receptacle);
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

        InteractableDeactivateMethod?.Invoke(receptacle, [false]);

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
        if (ReceptacleUnlockField?.GetValue(receptacle) is Object unlock && unlock != null) {
            unlock.GetType().GetMethod("Open", InstanceFlags, null, Type.EmptyTypes, null)?.Invoke(unlock, null);
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
