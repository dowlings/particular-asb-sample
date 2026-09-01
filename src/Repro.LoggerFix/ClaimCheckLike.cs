using ILog = NServiceBus.Logging.ILog;
using LogManager = NServiceBus.Logging.LogManager;

namespace Repro.LoggerFix;

/// <summary>
/// Stands in for <c>BlobStorageClaimCheck</c>, which holds a static <c>ILog</c> and logs a
/// single line from <see cref="IDisposable.Dispose"/>:
/// <code>
/// public void Dispose() => logger.Info("Blob storage data bus stopped");
/// static ILog logger = LogManager.GetLogger(typeof(IClaimCheck));
/// </code>
/// Container disposal never runs inside an endpoint log slot, so this write goes to
/// LogManager's fallback logger - the rolling file logger - rather than the host's
/// providers. Reproduced here without needing Azure Blob Storage.
/// </summary>
sealed class ClaimCheckLike : IDisposable
{
    static readonly ILog logger = LogManager.GetLogger("Repro.ClaimCheckLike");

    public void Dispose() => logger.Info("Blob storage data bus stopped");
}
