using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
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
            FileCheck("FFmpeg", options.FfmpegExecutablePath ?? string.Empty),
            FileCheck("FFprobe", RenderOutputWatcher.ResolveFfprobePath(options)),
            FileCheck("DemoCompatibilityRepair", DemoCompatibilityRepairer.ResolveExecutablePath(options)),
            CheckNetConPort(options.NetConPort),
            CheckProcessNotRunning("CS2NotRunning", "cs2"),
            CheckProcessNotRunning("HLAENotRunning", "HLAE"),
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

    private static EnvironmentCheck CheckNetConPort(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return Check("NetConPortAvailable", false, $"Port {port} is outside the valid TCP range.");
        }

        TcpListener listener = new(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return Check("NetConPortAvailable", true, $"127.0.0.1:{port}");
        }
        catch (SocketException exception)
        {
            return Check("NetConPortAvailable", false, exception.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static EnvironmentCheck CheckProcessNotRunning(string checkName, string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return Check(
                checkName,
                processes.Length == 0,
                processes.Length == 0
                    ? $"{processName}.exe is not running."
                    : $"{processName}.exe is already running (PID: {string.Join(", ", processes.Select(process => process.Id))}).");
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static EnvironmentCheck Check(string name, bool success, string message) => new(name, success, message);
}
