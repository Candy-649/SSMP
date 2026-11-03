using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
// ReSharper disable InconsistentNaming
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace SSMP.Game.Client.Entity; 

/// <summary>
/// Snapshot of FSM that includes current state and current values for all FSM variables.
/// Used to check for any changes in state/variables to network with the server.
/// </summary>
internal class FsmSnapshot {
    /// <summary>
    /// The name of the current (or last) state of the FSM.
    /// </summary>
    public string CurrentState { get; set; }
    
    /// <summary>
    /// Dictionary of names of float variables and corresponding (current/last) value.
    /// </summary>
    public float[] Floats { get; init; }
    /// <summary>
    /// Dictionary of names of int variables and corresponding (current/last) value.
    /// </summary>
    public int[] Ints { get; init; }
    /// <summary>
    /// Dictionary of names of bool variables and corresponding (current/last) value.
    /// </summary>
    public bool[] Bools { get; init; }
    /// <summary>
    /// Dictionary of names of string variables and corresponding (current/last) value.
    /// </summary>
    public string[] Strings { get; init; }
    /// <summary>
    /// Dictionary of names of vector2 variables and corresponding (current/last) value.
    /// </summary>
    public Vector2[] Vector2s { get; init; }
    /// <summary>
    /// Dictionary of names of vector3 variables and corresponding (current/last) value.
    /// </summary>
    public Vector3[] Vector3s { get; init; }

    /// <summary>
    /// Whether what the FSM of the room's own creature holds went to the other game while going into a state was still
    /// under way (Entity.SendStateChange), so that the next look at the FSM sends all of its variables that changed,
    /// with what the rest of going into the state set (see Entity.AddVariableChanges).
    /// </summary>
    public bool VariablesOwed { get; set; }

    /// <summary>
    /// The actions of the FSM of the room's own creature whose replays went to the other game in the state it is in,
    /// or was in last, for the variables that the other game reads while it runs them (see Entity.NoteReplayed).
    /// </summary>
    public List<HookedEntityAction> Replayed { get; } = [];

    /// <summary>
    /// For each state of the FSM of the room's own creature that it was in, the numbers and points that the copy reads
    /// as it answers a wound of its player by itself from there (see Entity.WoundReadsOf).
    /// </summary>
    public Dictionary<FsmState, NumbersRead> ReadByWounds { get; } = [];
}
