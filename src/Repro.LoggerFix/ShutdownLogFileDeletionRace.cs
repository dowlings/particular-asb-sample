using System.Diagnostics;

namespace Repro.LoggerFix;

enum ShutdownFailureMode
{
    /// <summary>
    /// Remove the log directory during shutdown. Deterministic: the enumeration in
    /// <c>SyncFileSystem</c> throws <see cref="DirectoryNotFoundException"/> every time.
    /// </summary>
    RemoveLogDirectory,

    /// <summary>
    /// Create and delete the file the logger is about to measure. Reproduces the customer's
    /// exact <see cref="FileNotFoundException"/> at <c>CalculateNewFileName</c>, but only
    /// when the deletion lands between the directory listing and the size check, so it needs
    /// several attempts.
    /// </summary>
    DeleteLogFileRace
}

sealed class ShutdownLogFileDeletionRaceSettings
{
    public required string LogDirectory { get; init; }

    public ShutdownFailureMode Mode { get; init; } = ShutdownFailureMode.RemoveLogDirectory;

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Files seeded before shutdown, used by <see cref="ShutdownFailureMode.DeleteLogFileRace"/>.</summary>
    public int SeedFileCount { get; init; } = 400;

    /// <summary>How many of the highest-numbered files to churn during shutdown.</summary>
    public int ChurnedFileCount { get; init; } = 24;
}

/// <summary>
/// Disturbs the rolling logger's directory during shutdown, so that the first write from
/// <see cref="ClaimCheckLike"/> fails while choosing a file name.
/// </summary>
/// <remarks>
/// Both modes exercise the same defect: <c>RollingLogger.WriteLine</c> calls
/// <c>SyncFileSystem()</c> outside the try/catch that guards the write, so a failure while
/// listing the directory or measuring a file escapes into the caller.
/// <para>
/// On Azure App Service the disturbance comes from outside the process. wwwroot is an Azure
/// Files share mounted into every instance, so instances rotate and purge the same file set
/// concurrently.
/// </para>
/// </remarks>
sealed class ShutdownLogFileDeletionRace(
    IHostApplicationLifetime applicationLifetime,
    ShutdownLogFileDeletionRaceSettings settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Mode == ShutdownFailureMode.DeleteLogFileRace)
        {
            SeedLogFiles();
        }

        applicationLifetime.ApplicationStopping.Register(Disturb);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    string LogFilePath(int sequenceNumber) => Path.Combine(
        settings.LogDirectory,
        $"nsb_log_{DateTimeOffset.Now:yyyy-MM-dd}_{sequenceNumber}.txt");

    void SeedLogFiles()
    {
        for (var i = 0; i < settings.SeedFileCount; i++)
        {
            File.WriteAllText(LogFilePath(i), "shutdown race seed");
        }
    }

    void Disturb()
    {
        if (settings.Mode == ShutdownFailureMode.RemoveLogDirectory)
        {
            Directory.Delete(settings.LogDirectory, recursive: true);
            return;
        }

        StartRace();
    }

    void StartRace()
    {
        // GetTodaysNewest picks the highest sequence number present when the directory is
        // listed, so churning only the highest mostly misses - if it happens to be absent at
        // that moment a stable lower-numbered file is chosen and the size check succeeds.
        // Churning the whole top of the range means whichever file is picked is unstable too.
        using var started = new CountdownEvent(settings.ChurnedFileCount);

        for (var offset = 0; offset < settings.ChurnedFileCount; offset++)
        {
            var sequenceNumber = settings.SeedFileCount - 1 - offset;
            raceTasks.Add(Task.Factory.StartNew(
                () => RunRace(started, sequenceNumber),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));
        }

        started.Wait();
    }

    void RunRace(CountdownEvent started, int sequenceNumber)
    {
        var target = LogFilePath(sequenceNumber);
        var stopwatch = Stopwatch.StartNew();

        started.Signal();

        while (stopwatch.Elapsed < settings.Duration)
        {
            try
            {
                File.WriteAllText(target, "shutdown race seed");
                File.Delete(target);
            }
            catch (IOException)
            {
                // File access collisions are the condition this diagnostic scenario exercises.
            }
        }
    }

    readonly List<Task> raceTasks = [];
}
