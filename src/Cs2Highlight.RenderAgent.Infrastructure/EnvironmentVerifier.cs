using System.Runtime.InteropServices;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class EnvironmentVerifier(RenderEnvironmentOptions options) : IEnvironmentVerifier
{
    public Task<EnvironmentReport> VerifyAsync(RenderJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<EnvironmentCheck> checks =
        [
            Check("Windows", RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.OSDescription),
            FileCheck("HLAE", options.HlaeExecutablePath),
            FileCheck("AfxHookSource2", GetHookPath(options.HlaeExecutablePath)),
            FileCheck("CS2", options.Cs2ExecutablePath),
            FileCheck("Steam", options.SteamExecutablePath),
            FileCheck("DemoCompatibilityRepair", DemoCompatibilityRepairer.ResolveExecutablePath(options)),
            Check("AutomationVerified", options.AutomationVerified,
                options.AutomationVerified
                    ? "The operator marked this exact HLAE/CS2 command set as manually verified."
                    : "Set only after manually validating HLAE arguments and generated CFG against installed versions."),
            CheckDirectory("WorkingRoot", options.WorkingRoot),
            CheckDirectory("OutputDirectory", job.OutputDirectory),
            Check("InteractiveSession", Environment.UserInteractive,
                Environment.UserInteractive ? "Interactive session detected." : "No interactive desktop session.")
        ];

        DriveInfo? drive = DriveInfo.GetDrives()
            .FirstOrDefault(candidate => candidate.IsReady &&
                Path.GetFullPath(options.WorkingRoot).StartsWith(candidate.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase));
        checks.Add(Check("FreeDiskSpace", drive?.AvailableFreeSpace > 1_073_741_824,
            drive is null ? "Unable to determine drive." : $"{drive.AvailableFreeSpace} bytes available; at least 1 GiB required."));
        return Task.FromResult(new EnvironmentReport(checks));
    }

    private static EnvironmentCheck FileCheck(string name, string path) =>
        Check(name, !string.IsNullOrWhiteSpace(path) && File.Exists(path),
            string.IsNullOrWhiteSpace(path) ? "Path is not configured." : path);

    private static string GetHookPath(string hlaeExecutablePath) =>
        string.IsNullOrWhiteSpace(hlaeExecutablePath)
            ? string.Empty
            : HlaeLauncher.GetHookDllPath(hlaeExecutablePath);

    private static EnvironmentCheck CheckDirectory(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Check(name, false, "Path is not configured.");
        }

        try
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return Check(name, true, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Check(name, false, exception.Message);
        }
    }

    private static EnvironmentCheck Check(string name, bool success, string message) => new(name, success, message);
}
