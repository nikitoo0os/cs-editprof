using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;
using Microsoft.Extensions.Logging;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class StateJournal(
    TimeProvider timeProvider,
    ILogger<StateJournal>? logger = null) : IStateJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Action<ILogger, RenderState, string, string, Exception?> LogRenderState =
        LoggerMessage.Define<RenderState, string, string>(
            LogLevel.Information,
            new EventId(3001, nameof(LogRenderState)),
            "[Render:{State}] {Message} (workspace: {Workspace})");

    public async Task WriteAsync(RenderWorkspace workspace, RenderState state, string message, CancellationToken cancellationToken)
    {
        if (logger is not null)
            LogRenderState(logger, state, message, workspace.Root, null);
        var entry = new { state, timestamp = timeProvider.GetUtcNow(), diagnosticMessage = message };
        string current = Path.Combine(workspace.State, "render-state.json");
        await File.WriteAllTextAsync(current, JsonSerializer.Serialize(entry, JsonOptions), cancellationToken);
        string history = Path.Combine(workspace.State, "render-state.jsonl");
        await File.AppendAllTextAsync(history, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken);
    }
}
