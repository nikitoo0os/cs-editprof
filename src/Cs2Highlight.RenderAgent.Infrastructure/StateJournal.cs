using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class StateJournal(TimeProvider timeProvider) : IStateJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task WriteAsync(RenderWorkspace workspace, RenderState state, string message, CancellationToken cancellationToken)
    {
        var entry = new { state, timestamp = timeProvider.GetUtcNow(), diagnosticMessage = message };
        string current = Path.Combine(workspace.State, "render-state.json");
        await File.WriteAllTextAsync(current, JsonSerializer.Serialize(entry, JsonOptions), cancellationToken);
        string history = Path.Combine(workspace.State, "render-state.jsonl");
        await File.AppendAllTextAsync(history, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken);
    }
}
