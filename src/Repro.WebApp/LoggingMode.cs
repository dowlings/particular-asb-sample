namespace Repro.WebApp;

/// <summary>
/// The variants under test. Select one with the <c>LoggingMode</c> configuration
/// key or the <c>LoggingMode</c> environment variable.
/// </summary>
enum LoggingMode
{
    /// <summary>Exactly the code on the ticket. No logging wire-up before the host is built.</summary>
    Customer,

    /// <summary>The fix suggested on the ticket: <c>builder.Logging.AddProvider(NullLoggerProvider.Instance)</c>.</summary>
    NullLoggerProvider,

    /// <summary>Redirect the rolling file provider via <c>Configure&lt;RollingLoggerProviderOptions&gt;</c>.</summary>
    RollingLoggerOptions,

    /// <summary>Redirect via the obsolete <c>LogManager.Use&lt;DefaultFactory&gt;().Directory(...)</c>.</summary>
    DefaultFactoryDirectory,

    /// <summary>Point NServiceBus at a Microsoft.Extensions.Logging factory before anything else runs.</summary>
    LogManagerUseFactory
}

static class LoggingModes
{
    public static LoggingMode Parse(string? value) =>
        Enum.TryParse<LoggingMode>(value, ignoreCase: true, out var mode) ? mode : LoggingMode.Customer;
}
