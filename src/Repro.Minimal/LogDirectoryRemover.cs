namespace Repro.Minimal;

sealed class LogDirectoryRemover(string directory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Directory.Delete(directory, true);
        return Task.CompletedTask;
    }
}
