using System;
using System.Globalization;
using SSMP;

namespace SSMP.Logging;

/// <summary>
/// Logger class for logging to BepInEx.
/// </summary>
public class BepInExLogger : BaseLogger {
    /// <summary>
    /// The BepInEx manual log source to log information to.
    /// </summary>
    private readonly BepInEx.Logging.ManualLogSource _logSource;

    /// <summary>
    /// Creates the logger and its log source, which is what names this mod in BepInEx's own log.
    /// </summary>
    public BepInExLogger() {
        _logSource = BepInEx.Logging.Logger.CreateLogSource(SSMPPlugin.Name);
    }

    /// <summary>
    /// The time of day as a line is written, to the thousandth of a second, which goes at the start of every line. The
    /// game's own log puts none there, and how long one thing came after another could be read only off the few lines
    /// that carried a time of their own: not how long a boss went without attacking (USER 2026-10-21: "为啥不打时间戳啊").
    /// </summary>
    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    
    /// <inheritdoc />
    public override void Info(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogInfo($"{Stamp()} [{GetOriginClassName()}] {message}");
    }

    /// <inheritdoc />
    public override void Message(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogMessage($"{Stamp()} [{GetOriginClassName()}] {message}");
    }

    /// <inheritdoc />
    public override void Debug(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogDebug($"{Stamp()} [{GetOriginClassName()}] {message}");
    }

    /// <inheritdoc />
    public override void Warn(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogWarning($"{Stamp()} [{GetOriginClassName()}] {message}");
    }

    /// <inheritdoc />
    public override void Error(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogError($"{Stamp()} [{GetOriginClassName()}] {message}");
    }
}
