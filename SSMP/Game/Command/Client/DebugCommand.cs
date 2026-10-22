using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Logging;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for executing debug-related things.
/// </summary>
internal class DebugCommand : IClientCommand, ICommandWithDescription {
    /// <inheritdoc />
    public string Trigger => "/debug";

    /// <inheritdoc />
    public string[] Aliases => ["/dbg"];
    
    /// <inheritdoc />
    public string Description => "Output various debug information to the log.";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        OutputBindFsmStateNames();
    }

    private void OutputBindFsmStateNames() {
        var heroFsms = HeroController.instance.GetComponents<PlayMakerFSM>();
        foreach (var heroFsm in heroFsms) {
            if (heroFsm.FsmName == "Bind") {
                for (var i = 0; i < heroFsm.FsmStates.Length; i++) {
                    var state = heroFsm.FsmStates[i];

                    Logger.Info($"{i}: {state.name}");
                }

                break;
            }
        }
    }

    private void Test() {
        tk2dSpriteAnimationClip clip = null!;

        Logger.Info($"Animation clip: {clip.name}, fps: {clip.fps}, frames length: {clip.frames.Length}");
        for (var i = 0; i < clip.frames.Length; i++) {
            var frame = clip.frames[i];
            if (frame.triggerEvent) {
                var secondsUntilFrame = (i + 1) / clip.fps;

                Logger.Info($"Frame {i} has triggerEvent, seconds until frame: {secondsUntilFrame}");
            }
        }
    }
}
