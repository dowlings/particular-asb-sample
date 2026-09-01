using ILog = NServiceBus.Logging.ILog;
using LogManager = NServiceBus.Logging.LogManager;

namespace Repro.WebApp;

/// <summary>
/// A static <c>ILog</c> held by a component, which is how NServiceBus itself and a
/// lot of NServiceBus-era application code obtain loggers.
/// </summary>
/// <remarks>
/// Writes through this logger do not happen inside an endpoint "log slot", so
/// <c>LogManager</c> routes them to its own fallback factory rather than to the
/// host's configured providers. That fallback is the thing that writes
/// <c>nsb_log_*.txt</c> to disk.
/// </remarks>
static class ReproLog
{
    public static readonly ILog OutOfSlot = LogManager.GetLogger("Repro.OutOfSlotComponent");
}
