namespace Repro.WebApp;

/// <summary>
/// Mirrors the shape of the customer's AppSettings, trimmed to the members that
/// matter for the logging repro. Setting names match theirs (including the
/// Functions-style <c>AzureWebJobsServiceBus</c>) so the sample reads the same.
/// </summary>
sealed class AppSettings
{
    public string EndpointName { get; set; } = "Samples.Repro.WebApp";

    public string WebAppName { get; set; } = "Repro.WebApp";

    public string EnvironmentName { get; set; } = "Development";

    /// <summary>
    /// Azure Service Bus connection string. When empty the sample falls back to
    /// the learning transport so the repro runs with no Azure resources at all.
    /// </summary>
    public string? AzureWebJobsServiceBus { get; set; }

    public string? APPLICATIONINSIGHTS_CONNECTION_STRING { get; set; }

    /// <summary>
    /// Which logging wire-up to exercise. See <see cref="LoggingMode"/>.
    /// </summary>
    public string? LoggingMode { get; set; }
}
