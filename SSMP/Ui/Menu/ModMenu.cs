using System;
using System.Collections;
using System.Collections.Generic;
using SSMP.Game.Client;
using SSMP.Game.Server;
using SSMP.Game.Settings;
using SSMP.Networking.Client;
using SSMP.Util;
using UnityEngine;
using UnityEngine.UI;
// ReSharper disable CollectionNeverUpdated.Local
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
// ReSharper disable NotAccessedField.Local
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
#pragma warning disable CS0169 // Field is never used

namespace SSMP.Ui.Menu;

/// <summary>
/// Class for building the SSMP mod menu.
/// </summary>
internal class ModMenu {
    /// <summary>
    /// The time in seconds that a modified setting needs to be stopped being modified by the user before being
    /// applied.
    /// </summary>
    private const float SettingApplyDelay = 1.5f;

    /// <summary>
    /// The SSMP mod settings instance.
    /// </summary>
    private readonly ModSettings _modSettings;
    
    /// <summary>
    /// The client manager instance.
    /// </summary>
    private readonly ClientManager _clientManager;

    /// <summary>
    /// The server manager instance.
    /// </summary>
    private readonly ServerManager _serverManager;

    /// <summary>
    /// The net client instance.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// List of callbacks that should be fired if the server settings change.
    /// </summary>
    private readonly List<Action<ServerSettings>> _serverSettingsChangedCallbacks;
    
    /// <summary>
    /// The menu containing the client settings. Needs to be a static variable here to allow it to be accessed by
    /// lambdas and modified.
    /// </summary>
    private MenuScreen _clientSettingsMenu;

    /// <summary>
    /// The menu containing the server settings. Needs to be a static variable here to allow it to be accessed by
    /// lambdas and modified.
    /// </summary>
    private MenuScreen _serverSettingsMenu;

    /// <summary>
    /// A local copy of the server settings for modification through the menu that will be used to either network to
    /// the server or modify our own hosted servers.
    /// </summary>
    private readonly ServerSettings _localServerSettings;

    /// <summary>
    /// Coroutine that delays applying new server settings until no more changes are made within a certain time period.
    /// </summary>
    private Coroutine _delayedApplyServerSettingsRoutine;

    /// <summary>
    /// Coroutine that delays applying the team setting until no more changes are made within a certain time period.
    /// </summary>
    private Coroutine _delayedApplyTeamRoutine;
    
    /// <summary>
    /// Coroutine that delays applying the skin setting until no more changes are made within a certain time period.
    /// </summary>
    private Coroutine _delayedApplySkinRoutine;

    /// <summary>
    /// The horizontal option for changing teams.
    /// </summary>
    private MenuOptionHorizontal _teamHorizontalOption;
    
    /// <summary>
    /// The horizontal option for changing skins.
    /// </summary>
    private MenuOptionHorizontal _skinHorizontalOption;

    /// <summary>
    /// Whether the horizontal option for changing teams is enabled (as in, whether the user can modify their team).
    /// </summary>
    private bool _teamHorizontalOptionEnabled;
    
    /// <summary>
    /// Whether the horizontal option for changing skins is enabled (as in, whether the user can modify their skin).
    /// </summary>
    private bool _skinHorizontalOptionEnabled;

    /// <summary>
    /// Constructs the mod menu class for SSMP.
    /// </summary>
    /// <param name="modSettings">The mod settings for SSMP.</param>
    /// <param name="clientManager">The client manager to register a callback for when server settings are updated.
    /// </param>
    /// <param name="serverManager">The server manager to get the initial server settings and update them if we are
    /// not connected to a server.</param>
    /// <param name="netClient">The net client to network changes to the server settings if we are connected to a
    /// server.</param>
    public ModMenu(
        ModSettings modSettings, 
        ClientManager clientManager, 
        ServerManager serverManager, 
        NetClient netClient
    ) {
        _modSettings = modSettings;
        _clientManager = clientManager;
        _serverManager = serverManager;
        _netClient = netClient;

        _serverSettingsChangedCallbacks = [];
    }

    /// <summary>
    /// Run the given action after a delay (defined in <see cref="SettingApplyDelay"/>) and store the delayed execution coroutine in the referenced
    /// variable. If this method is called again with the same referenced coroutine before the action is executed,
    /// it will be cancelled and a new delayed execution is scheduled.
    /// Can be used to apply the changing of certain settings only if the user stops modifying the setting for a
    /// certain period of time.
    /// </summary>
    /// <param name="action">The action to execute delayed if this method is not called again within a certain period
    /// of time.</param>
    /// <param name="coroutine">The coroutine containing the previous delayed execution of the action. This variable
    /// will be used to store the new coroutine.</param>
    private void RunActionWhenNoChanges(Action action, ref Coroutine coroutine) {
        if (coroutine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(coroutine);
        }

        coroutine = MonoBehaviourUtil.Instance.StartCoroutine(RunActionWithDelay(action, SettingApplyDelay));
    }

    /// <summary>
    /// Handles updates to the server settings. Checks whether we are connected to a server and only sends updates
    /// if no changes were made to the local server settings in this class for a certain period of time to prevent
    /// sending unnecessary updates.
    /// </summary>
    private void HandleUpdateServerSettings() {
        if (!_netClient.IsConnected) {
            // If the client is not connected to any server, we simply update the server settings of the 
            // server manager so that when we host a server, it will have the same settings as in this menu
            _serverManager.InternalServerSettings.SetAllProperties(_localServerSettings);
            return;
        }
        
        RunActionWhenNoChanges(() => {
            _netClient.UpdateManager.SetServerSettingsUpdate(_localServerSettings);
        }, ref _delayedApplyServerSettingsRoutine);
    }

    /// <summary>
    /// Callback method for the client connection changes, when either connected or disconnected from a server.
    /// This will modify the team selection depending on whether team selection should be allowed or not.
    /// </summary>
    /// <param name="connected"></param>
    private void OnClientConnectionChange(bool connected) {
        if (!connected) {
            ModifyTeamHorizontalOption(false);
            ModifySkinHorizontalOption(false);
            return;
        }

        if (_localServerSettings.TeamsEnabled) {
            ModifyTeamHorizontalOption(true);
        }

        if (_localServerSettings.AllowSkins) {
            ModifySkinHorizontalOption(true);
        }
    }

    /// <summary>
    /// Modify the horizontal option for teams to either allow or disallow team selection.
    /// </summary>
    /// <param name="allowTeams">Whether to allow the player to select a team.</param>
    private void ModifyTeamHorizontalOption(bool allowTeams) {
    }
    
    /// <summary>
    /// Modify the horizontal option for skins to either allow or disallow skin selection.
    /// </summary>
    /// <param name="allowSkins">Whether to allow the player to select a skin.</param>
    private void ModifySkinHorizontalOption(bool allowSkins) {
    }

    /// <summary>
    /// Run the given action with the given delay.
    /// </summary>
    /// <param name="action">The action to invoke after the delay.</param>
    /// <param name="delay">The delay in seconds.</param>
    /// <returns>The coroutine of the delayed invocation.</returns>
    private static IEnumerator RunActionWithDelay(Action action, float delay) {
        yield return new WaitForSeconds(delay);
        
        action.Invoke();
    }
}
