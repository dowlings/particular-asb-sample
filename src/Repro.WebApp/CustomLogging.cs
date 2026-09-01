using ILog = NServiceBus.Logging.ILog;
using LoggingFactoryDefinition = NServiceBus.Logging.LoggingFactoryDefinition;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;
using NsbLoggerFactory = NServiceBus.Logging.ILoggerFactory;

namespace Repro.WebApp;

/// <summary>
/// Routes NServiceBus logging to Microsoft.Extensions.Logging without the
/// NServiceBus.Extensions.Logging package - the same job ExtensionsLoggerFactory
/// does, in about 40 lines you own.
/// </summary>
#pragma warning disable CS0618 // LoggingFactoryDefinition is deprecated in core 10.2
sealed class MelLoggingFactoryDefinition : LoggingFactoryDefinition
{
    /// <summary>Set before calling <c>LogManager.Use&lt;MelLoggingFactoryDefinition&gt;()</c>.</summary>
    public static ILoggerFactory? Factory;

    protected override NsbLoggerFactory GetLoggingFactory() =>
        new MelLoggerFactory(Factory ?? throw new InvalidOperationException($"Set {nameof(Factory)} first."));
}
#pragma warning restore CS0618

sealed class MelLoggerFactory(ILoggerFactory inner) : NsbLoggerFactory
{
    public ILog GetLogger(Type type) => GetLogger(type.FullName ?? type.Name);

    public ILog GetLogger(string name) => new MelLog(inner.CreateLogger(name));
}

sealed class MelLog(ILogger logger) : ILog
{
    public bool IsDebugEnabled => logger.IsEnabled(MelLogLevel.Debug);
    public bool IsInfoEnabled => logger.IsEnabled(MelLogLevel.Information);
    public bool IsWarnEnabled => logger.IsEnabled(MelLogLevel.Warning);
    public bool IsErrorEnabled => logger.IsEnabled(MelLogLevel.Error);
    public bool IsFatalEnabled => logger.IsEnabled(MelLogLevel.Critical);

    // The message is passed as a value, not a template, so braces in NServiceBus
    // messages are never parsed as structured-logging placeholders.
    void Write(MelLogLevel level, string? message, Exception? exception = null) =>
        logger.Log(level, exception, "{NServiceBusMessage}", message);

    void WriteFormat(MelLogLevel level, string format, object?[] args) =>
        Write(level, string.Format(format, args));

    public void Debug(string? message) => Write(MelLogLevel.Debug, message);
    public void Debug(string? message, Exception? exception) => Write(MelLogLevel.Debug, message, exception);
    public void DebugFormat(string format, params object?[] args) => WriteFormat(MelLogLevel.Debug, format, args);

    public void Info(string? message) => Write(MelLogLevel.Information, message);
    public void Info(string? message, Exception? exception) => Write(MelLogLevel.Information, message, exception);
    public void InfoFormat(string format, params object?[] args) => WriteFormat(MelLogLevel.Information, format, args);

    public void Warn(string? message) => Write(MelLogLevel.Warning, message);
    public void Warn(string? message, Exception? exception) => Write(MelLogLevel.Warning, message, exception);
    public void WarnFormat(string format, params object?[] args) => WriteFormat(MelLogLevel.Warning, format, args);

    public void Error(string? message) => Write(MelLogLevel.Error, message);
    public void Error(string? message, Exception? exception) => Write(MelLogLevel.Error, message, exception);
    public void ErrorFormat(string format, params object?[] args) => WriteFormat(MelLogLevel.Error, format, args);

    public void Fatal(string? message) => Write(MelLogLevel.Critical, message);
    public void Fatal(string? message, Exception? exception) => Write(MelLogLevel.Critical, message, exception);
    public void FatalFormat(string format, params object?[] args) => WriteFormat(MelLogLevel.Critical, format, args);
}
