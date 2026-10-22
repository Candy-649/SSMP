using System;
using System.Diagnostics;

namespace SSMP.Logging;

/// <summary>
/// Abstract base class for loggers that prepends messages with their log level and origin class.
/// </summary>
public abstract class BaseLogger : ILogger {
    /// <summary>
    /// Get the name of the class whose code called the log function in which this method is used. Will skip
    /// classes in the stack frame that are within the "SSMP.Logging" namespace. The name goes without its namespace,
    /// which took up a third of every line. Code in a lambda, a local function or an iterator runs in a class that the
    /// compiler makes inside the class it is written in and names with angle brackets, like
    /// &lt;&gt;c__DisplayClass23_0, and its lines are said to come from the class it is written in.
    /// Note that this method is prone to breaking if namespace changes or stack frame changes for logging occur.
    /// </summary>
    /// <returns>The name of the origin class, after the class it is in if any, or the name of the method if no such
    /// class exists.</returns>
    protected static string GetOriginClassName() {
        Type? declaringType;
        var skipFrames = 3;

        do {
            var methodBase = new StackFrame(skipFrames, false).GetMethod();

            declaringType = methodBase.DeclaringType;
            if (declaringType == null) {
                return methodBase.Name;
            }

            skipFrames++;
        } while (
            declaringType.Module.Name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
            || (declaringType.Namespace != null && declaringType.Namespace.StartsWith("SSMP.Logging"))
        );

        while (declaringType.DeclaringType != null && declaringType.Name.StartsWith("<")) {
            declaringType = declaringType.DeclaringType;
        }

        var name = NameWithoutArity(declaringType);
        return declaringType.DeclaringType == null
            ? name
            : NameWithoutArity(declaringType.DeclaringType) + "." + name;
    }

    /// <summary>
    /// The name of a type without the number of type parameters that the name of a generic type ends in.
    /// </summary>
    /// <param name="type">The type.</param>
    private static string NameWithoutArity(Type type) {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }

    /// <summary>
    /// Check if a message should be logged (not null, empty, or whitespace).
    /// </summary>
    /// <param name="message">The message to check.</param>
    /// <returns>True if the message should be logged, false otherwise.</returns>
    protected static bool ShouldLogMessage(string message) {
        return !string.IsNullOrWhiteSpace(message);
    }

    /// <inheritdoc />
    public abstract void Info(string message);

    /// <inheritdoc />
    public abstract void Message(string message);

    /// <inheritdoc />
    public abstract void Debug(string message);

    /// <inheritdoc />
    public abstract void Warn(string message);

    /// <inheritdoc />
    public abstract void Error(string message);
}
