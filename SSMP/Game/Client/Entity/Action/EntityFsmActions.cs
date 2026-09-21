using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Random = UnityEngine.Random;
using Logger = SSMP.Logging.Logger;

// ReSharper disable NotAccessedField.Local
// ReSharper disable CollectionNeverUpdated.Local
// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider
// adding the 'required' modifier or declaring as nullable.

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace SSMP.Game.Client.Entity.Action;

/// <summary>
/// Static class containing method that transform FSM actions into network-able data and applying networked data
/// into the FSM actions implementations. 
/// </summary>
internal static partial class EntityFsmActions {
    /// <summary>
    /// The prefix of a method name that transforms an FSM action into network-able data.
    /// </summary>
    private const string GetMethodNamePrefix = "Get";

    /// <summary>
    /// The prefix of a method name that applies network data into an FSM action.
    /// </summary>
    private const string ApplyMethodNamePrefix = "Apply";

    /// <summary>
    /// Binding flags for accessing the private static methods in this class.
    /// </summary>
    private const BindingFlags StaticNonPublicFlags = BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>
    /// Set containing types of actions that are supported for transformation by a method in this class.
    /// </summary>
    public static readonly HashSet<Type> SupportedActionTypes = [];

    /// <summary>
    /// Event that is called when an entity is spawned from an object.
    /// </summary>
    public static event Func<EntitySpawnDetails, bool> EntitySpawnEvent;

    /// <summary>
    /// Dictionary mapping a type of an FSM action to the corresponding method info of the "get" method in this class.
    /// </summary>
    private static readonly Dictionary<Type, MethodInfo> TypeGetMethodInfos = new();

    /// <summary>
    /// Dictionary mapping a type of an FSM action to the corresponding method info of the "apply" method in this class.
    /// </summary>
    private static readonly Dictionary<Type, MethodInfo> TypeApplyMethodInfos = new();

    /// <summary>
    /// Dictionary containing queues of objects for a FSM action that has been executed on a host entity.
    /// Used to log the results of random calls to network to clients.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmStateAction, Queue<object>> RandomActionValues = new();

    /// <summary>
    /// List of actions that are executing while in a state and need to be stopped again when the state is exited.
    /// </summary>
    private static readonly List<ActionInState> ActionsInState = [];

    /// <summary>
    /// ILHook for FlingObjectsFromGlobalPool.OnEnter.
    /// </summary>
    private static ILHook? _flingPoolHook;

    /// <summary>
    /// ILHook for FlingObjectsFromGlobalPoolVel.OnEnter.
    /// </summary>
    private static ILHook? _flingPoolVelHook;

    /// <summary>
    /// ILHook for FlingObjectsFromGlobalPoolTime.OnUpdate.
    /// </summary>
    private static ILHook? _flingPoolTimeUpdateHook;

    /// <summary>
    /// ILHook for FlingObjectsFromGlobalPoolTime.OnEnter (NOP emit).
    /// </summary>
    private static ILHook? _flingPoolTimeEnterNopHook;

    /// <summary>
    /// ILHook for GetRandomChild.DoGetRandomChild.
    /// </summary>
    private static ILHook? _getRandomChildHook;

    /// <summary>
    /// ILHook for SpawnBloodTime.OnEnter (NOP emit).
    /// </summary>
    private static ILHook? _spawnBloodTimeNopHook;

    /// <summary>
    /// Static constructor that initializes the set and dictionaries by checking all methods in the class.
    /// </summary>
    /// <exception cref="Exception"></exception>
    static EntityFsmActions() {
        var methodInfos = typeof(EntityFsmActions).GetMethods(StaticNonPublicFlags);

        foreach (var methodInfo in methodInfos) {
            var parameterInfos = methodInfo.GetParameters();
            if (parameterInfos.Length != 2) {
                // Can't be a method that gets or applies entity network data
                continue;
            }

            // Filter out the base methods
            var parameterType = parameterInfos[1].ParameterType;
            if (parameterType.IsAbstract || !parameterType.IsSubclassOf(typeof(FsmStateAction))) {
                continue;
            }

            SupportedActionTypes.Add(parameterType);

            if (methodInfo.Name.StartsWith(GetMethodNamePrefix)) {
                TypeGetMethodInfos.Add(parameterType, methodInfo);
            } else if (methodInfo.Name.StartsWith(ApplyMethodNamePrefix)) {
                TypeApplyMethodInfos.Add(parameterType, methodInfo);
            } else {
                throw new Exception("Method was defined that does not adhere to the method naming");
            }
        }

        _flingPoolHook = new ILHook(
            typeof(FlingObjectsFromGlobalPool).GetMethod(
                nameof(FlingObjectsFromGlobalPool.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )!,
            FlingObjectsFromGlobalPoolOnEnter
        );
        _flingPoolVelHook = new ILHook(
            typeof(FlingObjectsFromGlobalPoolVel).GetMethod(
                nameof(FlingObjectsFromGlobalPoolVel.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )!,
            FlingObjectsFromGlobalPoolVelOnEnter
        );
        _flingPoolTimeUpdateHook = new ILHook(
            typeof(FlingObjectsFromGlobalPoolTime).GetMethod(
                "OnUpdate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )!,
            FlingObjectsFromGlobalPoolTimeOnUpdate
        );
        _getRandomChildHook = new ILHook(
            typeof(GetRandomChild).GetMethod(
                "DoGetRandomChild",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )!,
            GetRandomChildOnDoGetRandomChild
        );

        // Register IL hooks for the OnEnter method of certain classes. These OnEnter methods do not
        // have a method body and thus no IL instructions (apart from ret). Hooking this in the FsmActionHooks class
        // will not work, so we emit a NOP instruction to the body to make it hookable
        _flingPoolTimeEnterNopHook = new ILHook(
            typeof(FlingObjectsFromGlobalPoolTime).GetMethod(
                nameof(FlingObjectsFromGlobalPoolTime.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )!,
            EmitNop
        );
        _spawnBloodTimeNopHook = new ILHook(
            typeof(SpawnBloodTime).GetMethod(
                nameof(SpawnBloodTime.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )!,
            EmitNop
        );
        return;

        // Register the IL hooks for modifying FSM action methods
        void EmitNop(ILContext il) => new ILCursor(il).Emit(OpCodes.Nop);
    }

    /// <summary>
    /// Gets network-able data from the given action and puts it in the given <see cref="EntityNetworkData"/> instance.
    /// </summary>
    /// <param name="data">The instance to put the data into.</param>
    /// <param name="action">The action to transform.</param>
    /// <returns>Whether from this action network-able data was made.</returns>
    /// <exception cref="InvalidOperationException">Thrown if there is no suitable method for the action and thus
    /// no network data is written.</exception>
    public static bool GetNetworkDataFromAction(EntityNetworkData data, FsmStateAction action) {
        var actionType = action.GetType();
        if (!TypeGetMethodInfos.TryGetValue(actionType, out var methodInfo)) {
            throw new InvalidOperationException(
                $"Given action type: {action.GetType()} does not have an associated method to get"
            );
        }

        var returnObject = methodInfo.Invoke(
            null,
            StaticNonPublicFlags,
            null,
            [data, action],
            null!
        );

        // Return whether the return object is a bool and has the value 'true'
        return returnObject is true;
    }

    /// <summary>
    /// Reads networked data from the given instance and mimics the execution of the given FSM action.
    /// </summary>
    /// <param name="data">The instance from which to get the data.</param>
    /// <param name="action">The FSM action to mimic execution for.</param>
    /// <exception cref="InvalidOperationException">Thrown if there is no suitable method for the action and thus
    /// no FSM action will be mimicked.</exception>
    public static void ApplyNetworkDataFromAction(EntityNetworkData data, FsmStateAction action) {
        var actionType = action.GetType();
        if (!TypeApplyMethodInfos.TryGetValue(actionType, out var methodInfo)) {
            throw new InvalidOperationException(
                $"Given action type: {action.GetType()} does not have an associated method to apply"
            );
        }

        if (ActsOnTheLocalPlayer(action)) {
            SayItWasKeptOffThePlayer(action);
            return;
        }

        try {
            methodInfo.Invoke(
                null,
                StaticNonPublicFlags,
                null,
                [data, action],
                null!
            );
        } catch (Exception e) {
            //Logger.Warn($"Apply method threw exception: {e.GetType()}, {e.Message}, {e.StackTrace}");

            e = e.InnerException;
            while (e != null) {
                //Logger.Warn($"  Inner exception: {e.GetType()}, {e.Message}, {e.StackTrace}");

                e = e.InnerException;
            }
        }
    }

    /// <summary>
    /// The types that only read the object they are given - look something up in it, take its position, aim at it or
    /// store it in a variable - so that being given the player character does nothing to it.
    /// </summary>
    private static readonly HashSet<string> ReadsWithoutActing = [
        "FindChild", "GetChild", "GetParent", "GetRandomChild", "GetOwner", "GetHero", "FindGameObject",
        "FindAlertRange", "GetPosition", "SetGameObject", "SpawnObjectFromGlobalPool", "FireAtTarget"
    ];

    /// <summary>
    /// The parameters of each action type that name the object it works on, by type.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> SubjectFields = new();

    /// <summary>
    /// The creatures' actions already named for being kept off the local player, so each is named once.
    /// </summary>
    private static readonly HashSet<string> NamedForKeepingOffThePlayer = [];

    /// <summary>
    /// Whether a replayed action works on the local player's own character, or on something the character carries.
    ///
    /// Such an action was about the player character of the game that runs the creature. The FSM variable that holds
    /// "the player" is filled in on this copy too, with this game's own character, so replaying it here grabbed,
    /// froze, hid, turned, threw and animated whoever was standing in the room with the player it was really about -
    /// wherever they stood. Going through every such action in the game's data found about a thousand, in forty kinds
    /// of creature: a boss catching the scene host sent the scene client's character into the wounded pose too, a
    /// creature hiding the player it swallowed hid the scene client as well, and one that ends a grab by cancelling
    /// what the player is doing cancelled the scene client's healing, sprinting and skills. Nothing about the local
    /// player is decided by the other game: what can touch them is this game's own copies of the creature's parts,
    /// which run here.
    /// </summary>
    /// <param name="action">The action about to be replayed.</param>
    private static bool ActsOnTheLocalPlayer(FsmStateAction action) {
        if (action.Fsm == null || ReadsWithoutActing.Contains(action.GetType().Name)) {
            return false;
        }

        var hero = HeroController.instance;
        if (hero == null) {
            return false;
        }

        foreach (var field in GetSubjectFields(action.GetType())) {
            var target = field.GetValue(action) switch {
                FsmOwnerDefault owner => action.Fsm.GetOwnerDefaultTarget(owner),
                FsmGameObject gameObject => gameObject.Value,
                FsmEventTarget {
                    target: FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM
                } eventTarget => action.Fsm.GetOwnerDefaultTarget(eventTarget.gameObject),
                _ => null
            };

            if (target != null && target.transform.IsChildOf(hero.transform)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The fields of an action type that name the object it works on: the object it acts on, the parent it puts
    /// something under, and the object it sends an event to.
    /// </summary>
    private static FieldInfo[] GetSubjectFields(Type type) {
        if (SubjectFields.TryGetValue(type, out var fields)) {
            return fields;
        }

        var found = new List<FieldInfo>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            if (field.Name is not ("gameObject" or "parent" or "target" or "eventTarget")) {
                continue;
            }

            if (field.FieldType == typeof(FsmOwnerDefault) || field.FieldType == typeof(FsmGameObject) ||
                field.FieldType == typeof(FsmEventTarget)) {
                found.Add(field);
            }
        }

        fields = found.ToArray();
        SubjectFields[type] = fields;
        return fields;
    }

    /// <summary>
    /// Says, once per kind of action in a state, that an action about the other player's character was not done to
    /// the local one.
    /// </summary>
    private static void SayItWasKeptOffThePlayer(FsmStateAction action) {
        var key = $"{action.Fsm?.Name}/{action.State?.Name}/{action.GetType().Name}";
        if (!NamedForKeepingOffThePlayer.Add(key)) {
            return;
        }

        var owner = action.Fsm?.GameObject;
        Logger.Info(
            $"Not doing '{(owner == null ? "?" : owner.name)}'s {action.GetType().Name} in '{action.State?.Name}' to " +
            "the local player: it was about the other player's character"
        );
    }

    /// <summary>
    /// The variables holding the objects an action works on - its object parameter, the object it sends an event to
    /// and the parent it puts something under - in the order of its fields, leaving out any that is its owner or an
    /// object named directly. They follow from the action's own settings, which are the same in both games, so both
    /// ends agree on which objects are named in the data.
    /// </summary>
    private static List<FsmGameObject> SubjectVariables(FsmStateAction action) {
        var variables = new List<FsmGameObject>();
        if (ReadsWithoutActing.Contains(action.GetType().Name)) {
            return variables;
        }

        foreach (var field in GetSubjectFields(action.GetType())) {
            if (field.Name is not ("gameObject" or "eventTarget" or "parent")) {
                continue;
            }

            var value = field.GetValue(action);
            var owner = value switch {
                FsmOwnerDefault ownerDefault => ownerDefault,
                FsmEventTarget {
                    target: FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM
                } eventTarget => eventTarget.gameObject,
                _ => null
            };

            var variable = owner != null
                ? owner.OwnerOption == OwnerDefaultOption.SpecifyGameObject ? owner.GameObject : null
                : value as FsmGameObject;
            if (variable is { UseVariable: true }) {
                variables.Add(variable);
            }
        }

        return variables;
    }

    /// <summary>
    /// Writes, ahead of an action's own data, which objects the action worked on when they are held in variables: for
    /// each, 0 for none, 1 and its path for a part of the creature, 2 for an object anywhere else.
    ///
    /// The creature's FSM fills such variables in while it runs - which spray to use, which of its parts to light, which
    /// of them an event is for - and the scene client's copy never runs, so there the variable stayed empty and the
    /// action did nothing. Going through the game's data found 3792 of them in 106 kinds of creature: sprays, particle
    /// effects, sounds, events to their own parts, colliders of parts switched on and off, parts put under another
    /// (which an empty parent turned into parts dropped loose into the room). A part is named by its path under the
    /// creature, which is the same on the copy; anything else is left to the copy's own variable.
    /// </summary>
    public static void WriteSubject(EntityNetworkData data, FsmStateAction action) {
        foreach (var variable in SubjectVariables(action)) {
            var subject = variable.Value;
            if (subject == null) {
                data.Packet.Write((byte) 0);
                continue;
            }

            var path = PathFromOwner(action.Fsm.GameObject, subject);
            if (path == null) {
                data.Packet.Write((byte) 2);
                continue;
            }

            data.Packet.Write((byte) 1);
            data.Packet.Write(path);
        }
    }

    /// <summary>
    /// Reads what <see cref="WriteSubject"/> wrote and puts the objects into the copy's own variables, so that the
    /// action finds them there as it would in the game that runs the creature.
    /// </summary>
    public static void ReadSubject(EntityNetworkData data, FsmStateAction action) {
        foreach (var variable in SubjectVariables(action)) {
            switch (data.Packet.ReadByte()) {
                case 0:
                    variable.Value = null;
                    break;
                case 1:
                    var path = data.Packet.ReadString();
                    var owner = action.Fsm?.GameObject;
                    var named = owner == null ? null : path.Length == 0 ? owner.transform : owner.transform.Find(path);
                    variable.Value = named == null ? null : named.gameObject;
                    break;
            }
        }
    }

    /// <summary>
    /// Whether an action undoes itself when its state is left: switches an object or collider back, stops a particle
    /// emission or a tween. Checked in the game's own OnExit of each (IL).
    /// </summary>
    internal static bool UndoesOnExit(FsmStateAction action) => action switch {
        ActivateGameObject activate => activate.resetOnExit,
        ActivateGameObjectDelay activateDelay => activateDelay.resetOnExit,
        SetCollider collider => collider.resetOnExit,
        SetPolygonCollider polygonCollider => polygonCollider.resetOnExit,
        SetParticleEmission emission => emission.resetOnExit,
        iTweenFsmAction tween => tween.stopOnExit.IsNone || tween.stopOnExit.Value,
        _ => false
    };

    /// <summary>
    /// Undoes a replayed action once the scene host's FSM has left the state it was done in, as the action does itself
    /// in the game when told to (see <see cref="UndoesOnExit"/>). Only the doing was ever sent, never the undoing, so
    /// what a creature switched on for one attack stayed on here for good.
    /// </summary>
    /// <param name="action">The replayed action.</param>
    /// <param name="undo">What the action does when its state is left.</param>
    private static void UndoOnExit(FsmStateAction action, System.Action undo) {
        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            ExitAction = undo
        }.Register();
    }

    /// <summary>
    /// Checks whether the given game object is in the entity registry and can thus be registered as an entity in
    /// the system.
    /// </summary>
    /// <param name="gameObject">The game object to check for.</param>
    /// <returns>true if the given game object is in the entity registry; otherwise false.</returns>
    private static bool IsObjectInRegistry(GameObject gameObject) {
        return EntityRegistry.TryGetEntry(gameObject, out _);
    }

    /// <summary>
    /// The path of an object from the object an FSM runs on, as <see cref="Transform.Find(string)"/> takes it: empty
    /// for that object itself, or null for an object that is not under it.
    /// </summary>
    /// <param name="owner">The object the FSM runs on.</param>
    /// <param name="target">The object to name.</param>
    private static string? PathFromOwner(GameObject? owner, GameObject target) {
        if (owner == null) {
            return null;
        }

        var names = new List<string>();
        for (var current = target.transform; current != null; current = current.parent) {
            if (current == owner.transform) {
                names.Reverse();
                return string.Join("/", names);
            }

            names.Add(current.name);
        }

        return null;
    }

    /// <summary>
    /// Method to call the spawn event externally. TODO: refactor this into something more appropriate
    /// </summary>
    /// <param name="details">The spawn details for the event.</param>
    /// <returns>Whether an entity was registered from this spawn.</returns>
    public static void CallEntitySpawnEvent(EntitySpawnDetails details) {
        EntitySpawnEvent?.Invoke(details);
    }

    /// <summary>
    /// Emit intercept instruction on the next Unity Random Range() call for the given IL cursor.
    /// </summary>
    /// <param name="c">The cursor for the IL context of the method.</param>
    /// <typeparam name="TValue">The return type of the random call.</typeparam>
    /// <typeparam name="TObject">The type of the FSM state action in which the random call occurs.</typeparam>
    private static void EmitRandomInterceptInstructions<TValue, TObject>(ILCursor c)
        where TValue : notnull
        where TObject : FsmStateAction {
        // Goto the next call instruction for Random.Range()
        c.GotoNext(i => i.MatchCall(typeof(Random), "Range"));

        // Move the cursor after the call instruction
        c.Index++;

        // Push the current instance of the class onto the stack
        c.Emit(OpCodes.Ldarg_0);

        // Emit a delegate that pops the current random value off the stack and puts it back after some processing 
        c.EmitDelegate<Func<TValue, TObject, TValue>>((value, instance) => {
                // We need to check whether the game object that is being spawned with this action is not an object
                // managed by the system. Because if so, we do not store the random values because the action for it
                // is not being networked. Only the game object spawn is networked with an EntitySpawn packet directly.
                if (typeof(TObject).GetField(
                        "gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    )?.GetValue(instance) is FsmGameObject fsmGameObject && fsmGameObject.Value != null &&
                    IsObjectInRegistry(fsmGameObject.Value)) {
                    return value;
                }

                if (!RandomActionValues.TryGetValue(instance, out var queue)) {
                    queue = new Queue<object>();
                    RandomActionValues.Add(instance, queue);
                }

                queue.Enqueue(value);

                return value;
            }
        );
    }

    /// <summary>
    /// IL edit method for modifying the <see cref="FlingObjectsFromGlobalPool"/>
    /// <see cref="FlingObjectsFromGlobalPool.OnEnter"/> method to store the results of the random calls.
    /// </summary>
    private static void FlingObjectsFromGlobalPoolOnEnter(ILContext il) {
        try {
            // Create a cursor for this context
            var c = new ILCursor(il);

            // Emit instructions for Random.Range calls for 1 int and 4 floats 
            EmitRandomInterceptInstructions<int, FlingObjectsFromGlobalPool>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPool>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPool>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPool>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPool>(c);

            // Reset cursor
            c = new ILCursor(il);

            // Goto the next call instruction for ObjectPoolExtensions.Spawn
            c.GotoNext(i => i.MatchCall(typeof(ObjectPoolExtensions), "Spawn"));

            // Move the cursor after the call instruction
            c.Index++;

            // Push the current instance of the class onto the stack
            c.Emit(OpCodes.Ldarg_0);

            // Emit a delegate that pops the spawned game object off the stack and uses it, then puts it back again
            c.EmitDelegate<Func<GameObject, FlingObjectsFromGlobalPool, GameObject>>((go, action) => {
                    //Logger.Debug($"Delegate of FlingObjectsFromGlobalPool: {go.name}");
                    if (EntitySpawnEvent != null && EntitySpawnEvent.Invoke(
                            new EntitySpawnDetails {
                                Type = EntitySpawnType.FsmAction,
                                Action = action,
                                GameObject = go
                            }
                        )) {
                        //Logger.Debug("FlingObjectsFromGlobalPool IL spawned object is entity");
                    }

                    return go;
                }
            );
        } catch (Exception e) {
            Logger.Error($"Could not change FlingObjectsFromGlobalPool#OnEnter IL:\n{e}");
        }
    }

    /// <summary>
    /// IL edit method for modifying the <see cref="FlingObjectsFromGlobalPoolVel"/>
    /// <see cref="FlingObjectsFromGlobalPoolVel.OnEnter"/> method to store the results of the random calls.
    /// </summary>
    private static void FlingObjectsFromGlobalPoolVelOnEnter(ILContext il) {
        try {
            // Create a cursor for this context
            var c = new ILCursor(il);

            // Emit instructions for Random.Range calls for 1 int and 4 floats 
            EmitRandomInterceptInstructions<int, FlingObjectsFromGlobalPoolVel>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPoolVel>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPoolVel>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPoolVel>(c);
            EmitRandomInterceptInstructions<float, FlingObjectsFromGlobalPoolVel>(c);

            // Reset cursor
            c = new ILCursor(il);

            // Goto the next call instruction for ObjectPoolExtensions.Spawn
            c.GotoNext(i => i.MatchCall(typeof(ObjectPoolExtensions), "Spawn"));

            // Move the cursor after the call instruction
            c.Index++;

            // Push the current instance of the class onto the stack
            c.Emit(OpCodes.Ldarg_0);

            // Emit a delegate that pops the spawned game object off the stack and uses it, then puts it back again
            c.EmitDelegate<Func<GameObject, FlingObjectsFromGlobalPoolVel, GameObject>>((go, action) => {
                    Logger.Debug($"Delegate of FlingObjectsFromGlobalPoolVel: {go.name}");
                    if (EntitySpawnEvent != null && EntitySpawnEvent.Invoke(
                            new EntitySpawnDetails {
                                Type = EntitySpawnType.FsmAction,
                                Action = action,
                                GameObject = go
                            }
                        )) {
                        //Logger.Debug("FlingObjectsFromGlobalPoolVel IL spawned object is entity");
                    }

                    return go;
                }
            );
        } catch (Exception e) {
            Logger.Error($"Could not change FlingObjectsFromGlobalPoolVel#OnEnter IL:\n{e}");
        }
    }

    /// <summary>
    /// IL edit method for modifying the <see cref="FlingObjectsFromGlobalPoolTime"/>
    /// <see cref="FlingObjectsFromGlobalPoolTime.OnUpdate"/> method to network the repeated spawning of objects.
    /// </summary>
    private static void FlingObjectsFromGlobalPoolTimeOnUpdate(ILContext il) {
        try {
            // Create a cursor for this context
            var c = new ILCursor(il);

            // Goto the next call instruction for Random.Range()
            c.GotoNext(i => i.MatchCall(typeof(ObjectPoolExtensions), "Spawn"));

            // Move the cursor after the call instruction
            c.Index++;

            // Push the current instance of the class onto the stack
            c.Emit(OpCodes.Ldarg_0);

            // Emit a delegate that pops the spawned object off the stack and pushes it onto it again
            c.EmitDelegate<Func<GameObject, FlingObjectsFromGlobalPoolTime, GameObject>>((gameObject, action) => {
                    EntitySpawnEvent?.Invoke(
                        new EntitySpawnDetails {
                            Type = EntitySpawnType.FsmAction,
                            Action = action,
                            GameObject = gameObject
                        }
                    );

                    return gameObject;
                }
            );
        } catch (Exception e) {
            Logger.Error($"Could not change FlingObjectsFromGlobalPoolTime#OnUpdate IL:\n{e}");
        }
    }

    /// <summary>
    /// IL edit method for modifying the <see cref="GetRandomChild"/> DoGetRandomChild
    /// method to store the results of the random calls.
    /// </summary>
    private static void GetRandomChildOnDoGetRandomChild(ILContext il) {
        try {
            // Create a cursor for this context
            var c = new ILCursor(il);

            // Emit instructions for Random.Range calls for 1 int and 4 floats 
            EmitRandomInterceptInstructions<int, GetRandomChild>(c);
        } catch (Exception e) {
            Logger.Error($"Could not change GetRandomChild#DoGetRandomChild IL:\n{e}");
        }
    }

    /// <summary>
    /// Register a state change for the given FSM. Will propagate this change to all actions that are running in
    /// that state.
    /// </summary>
    /// <param name="fsm">The FSM that changed states.</param>
    /// <param name="stateName">The name of the state that was changed to.</param>
    public static void RegisterStateChange(HutongGames.PlayMaker.Fsm fsm, string stateName) {
        //Logger.Debug($"RegisterStateChange: {fsm.Name}, {stateName}");

        for (var i = ActionsInState.Count - 1; i >= 0; i--) {
            var actionInState = ActionsInState[i];

            //Logger.Debug($"  Action in state: {actionInState.Fsm.Name}, {actionInState.StateName}");

            if (actionInState.Fsm == fsm && actionInState.StateName != stateName) {
                //Logger.Debug("EntityFsmActions: state changed, cancelling action in state");
                actionInState.ExitState();
                ActionsInState.RemoveAt(i);
            }
        }
    }


    /// <summary>
    /// Class that keeps track of an action that executes while in a certain state of the FSM.
    /// </summary>
    private class ActionInState {
        /// <summary>
        /// The FSM of the action that is executing.
        /// </summary>
        public HutongGames.PlayMaker.Fsm Fsm { get; init; }

        /// <summary>
        /// The name of the state in which this action executes.
        /// </summary>
        public string StateName { get; init; }

        /// <summary>
        /// The coroutine that should be stopped when the state is exited.
        /// </summary>
        public Coroutine Coroutine { private get; init; }

        /// <summary>
        /// The action that should be executed when the state is exited.
        /// </summary>
        public System.Action ExitAction { private get; init; }

        /// <summary>
        /// Register this action by adding it to the list.
        /// </summary>
        public void Register() {
            ActionsInState.Add(this);
        }

        /// <summary>
        /// Call when the state is exited, will stop the coroutine and execute the exit action.
        /// </summary>
        public void ExitState() {
            if (Coroutine != null) {
                MonoBehaviourUtil.Instance.StopCoroutine(Coroutine);
            }

            ExitAction?.Invoke();
        }
    }
}
