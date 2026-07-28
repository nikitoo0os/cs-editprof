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

        List<string> arguments = SplitArguments(options.HlaeArguments);
        arguments.Add(script.Path);
        ProcessRequest request = new(
            options.HlaeExecutablePath,
            arguments,
            workspace.Root,
            Path.Combine(workspace.Logs, "hlae.stdout.log"),
            Path.Combine(workspace.Logs, "hlae.stderr.log"),
            TimeSpan.FromSeconds(options.ProcessStartupTimeoutSeconds),
            new Dictionary<string, string?> { ["USRLOCALCSGO"] = workspace.Config });
        return supervisor.RunAsync(request, cancellationToken);
    }

    internal static List<string> SplitArguments(string arguments)
    {
        List<string> result = [];
        bool quoted = false;
        System.Text.StringBuilder current = new();
        foreach (char character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (quoted)
        {
            throw new ArgumentException("HlaeArguments contains an unmatched quote.", nameof(arguments));
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }
}
