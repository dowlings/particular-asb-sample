using System.Diagnostics;

namespace Repro.VariantC;

sealed class FileChurn(IHostApplicationLifetime lifetime, string logDirectory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStopping.Register(Start);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void Start()
    {
        using var started = new ManualResetEventSlim();
        Task.Factory.StartNew(() => Run(started), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        started.Wait();
    }

    void Run(ManualResetEventSlim started)
    {
        var target = Path.Combine(logDirectory, $"nsb_log_{DateTimeOffset.Now:yyyy-MM-dd}_0.txt");
        var stopwatch = Stopwatch.StartNew();
        started.Set();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                File.WriteAllText(target, "churn");
                File.Delete(target);
            }
            catch (IOException)
            {
            }
        }
    }
}
