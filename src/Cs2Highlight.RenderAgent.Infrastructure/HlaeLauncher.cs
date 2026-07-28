using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class HlaeLauncher(RenderEnvironmentOptions options, IProcessSupervisor supervisor) : IHlaeLauncher
{
    public Task<ProcessExecutionResult> LaunchAsync(
        RenderWorkspace workspace,
        GeneratedRenderScript script,
        CancellationToken cancellationToken)
    {
        if (!options.AutomationVerified)
        {
            throw new InvalidOperationException("HLAE automation has not been manually verified.");
        }

        string hookDllPath = GetHookDllPath(options.HlaeExecutablePath);
        if (!File.Exists(hookDllPath))
        {
            throw new FileNotFoundException("AfxHookSource2.dll was not found next to HLAE.", hookDllPath);
        }

        IReadOnlyList<string> arguments = BuildArguments(options, workspace, script, hookDllPath);
        ProcessRequest request = new(
            options.HlaeExecutablePath,
            arguments,
            workspace.Root,
            Path.Combine(workspace.Logs, "hlae.stdout.log"),
            Path.Combine(workspace.Logs, "hlae.stderr.log"),
            TimeSpan.FromSeconds(options.ProcessStartupTimeoutSeconds));
        return supervisor.RunAsync(request, cancellationToken);
    }

    public static string GetHookDllPath(string hlaeExecutablePath)
    {
        string? hlaeDirectory = Path.GetDirectoryName(Path.GetFullPath(hlaeExecutablePath));
        if (hlaeDirectory is null)
        {
            throw new ArgumentException("HLAE executable path has no parent directory.", nameof(hlaeExecutablePath));
        }

        return Path.Combine(hlaeDirectory, "x64", "AfxHookSource2.dll");
    }

    public static IReadOnlyList<string> BuildArguments(
        RenderEnvironmentOptions environment,
        RenderWorkspace workspace,
        GeneratedRenderScript script,
        string hookDllPath)
    {
        if (environment.HlaeArguments.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException("HlaeArguments contains a forbidden control character.", nameof(environment));
        }

        string expectedScript = Path.Combine(workspace.Config, "cfg", "render.cfg");
        if (!Path.GetFullPath(script.Path).Equals(Path.GetFullPath(expectedScript), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Generated CFG is outside the isolated moviemaking config directory.");
        }

        string cs2CommandLine = string.Join(' ',
        [
            "-steam",
            "-insecure",
            "+sv_lan 1",
            "-console",
            "-sw",
            $"-w {script.Width}",
            $"-h {script.Height}",
            "-afxDisableSteamStorage",
            "+exec render.cfg",
            environment.HlaeArguments.Trim()
        ]).Trim();

        return
        [
            "-noConfig",
            "-customLoader",
            "-autoStart",
            "-noGui",
            "-hookDllPath",
            hookDllPath,
            "-programPath",
            environment.Cs2ExecutablePath,
            "-cmdLine",
            cs2CommandLine,
            "-addEnv",
            $"USRLOCALCSGO={workspace.Config}"
        ];
    }

}
