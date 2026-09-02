using NServiceBus.Logging;

namespace Repro.Concurrent;

sealed class ClaimCheck : IDisposable
{
    static readonly ILog log = LogManager.GetLogger("ClaimCheck");

    public void Dispose() => log.Info("Blob storage data bus stopped");
}
