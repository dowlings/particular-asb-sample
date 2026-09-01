using System.Text;

namespace Repro.WebApp;

/// <summary>
/// Looks for the files NServiceBus' rolling file provider writes
/// (<c>nsb_log_yyyy-MM-dd_N.txt</c>), so a run can state plainly whether the
/// problem reproduced.
/// </summary>
static class LogFileProbe
{
    const string Pattern = "nsb_log_*.txt";

    /// <summary>
    /// Where NServiceBus writes by default. <c>Host.GetOutputDirectory()</c> only
    /// does something different when System.Web is loaded, which never happens on
    /// .NET 10, so this resolves to the same directory the endpoint would use.
    /// </summary>
    public static string OutputDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>Target for the two "redirect the log file elsewhere" modes.</summary>
    public static string RedirectDirectory { get; } = CreateRedirectDirectory();

    public static void DeleteExistingLogFiles()
    {
        foreach (var file in Find(OutputDirectory).Concat(Find(RedirectDirectory)))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort - a stale handle should not stop the run.
            }
        }
    }

    public static IReadOnlyList<string> Find(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, Pattern).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public static string Report(LoggingMode mode, string when)
    {
        var output = Find(OutputDirectory);
        var redirected = Find(RedirectDirectory);

        var report = new StringBuilder();
        report.AppendLine("================ NServiceBus log file probe ================");
        report.AppendLine($"When            : {when}");
        report.AppendLine($"LoggingMode     : {mode}");
        report.AppendLine($"Output directory: {OutputDirectory}");
        report.AppendLine($"Redirect target : {RedirectDirectory}");
        report.AppendLine();
        report.AppendLine(output.Count == 0
            ? "REPRODUCED? no  - no nsb_log_*.txt in the output directory"
            : $"REPRODUCED? YES - {output.Count} nsb_log_*.txt file(s) in the output directory");

        foreach (var file in output)
        {
            report.AppendLine($"    {Path.GetFileName(file)}  ({new FileInfo(file).Length} bytes)");
        }

        if (redirected.Count > 0)
        {
            report.AppendLine($"Redirected      : {redirected.Count} file(s) under the redirect target");
            foreach (var file in redirected)
            {
                report.AppendLine($"    {Path.GetFileName(file)}  ({new FileInfo(file).Length} bytes)");
            }
        }

        report.AppendLine("============================================================");
        return report.ToString();
    }

    static string CreateRedirectDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nsb-logging-repro");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
