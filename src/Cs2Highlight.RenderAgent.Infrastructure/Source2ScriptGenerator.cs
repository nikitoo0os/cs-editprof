using System.Globalization;
using System.Text;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class Source2ScriptGenerator : IRenderScriptGenerator
{
    public async Task<GeneratedRenderScript> GenerateAsync(
        RenderJob job,
        RenderWorkspace workspace,
        CancellationToken cancellationToken)
    {
        string player = EscapeCfg(job.Player.SteamId ?? job.Player.Name ?? throw new InvalidOperationException("Missing player selector."));
        string demo = EscapeCfg(workspace.PreparedDemoPath);
        string raw = EscapeCfg(workspace.Raw);
        string cfgDirectory = Path.Combine(workspace.Config, "cfg");
        Directory.CreateDirectory(cfgDirectory);
        string path = Path.Combine(cfgDirectory, "render.cfg");
        StringBuilder cfg = new();
        cfg.AppendLine("// Generated for AfxHookSource2. Validate against the installed HLAE and CS2 builds.");
        cfg.AppendLine("mirv_cmd clear");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"mirv_fov {job.Video.Fov}");
        cfg.AppendLine("mirv_streams record screen enabled 1");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"mirv_streams record fps {job.Video.Fps}");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"mirv_streams record name \"{raw}\"");
        cfg.AppendLine("mirv_streams settings edit afxDefault settings afxFfmpeg");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"playdemo \"{demo}\"");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"mirv_cmd addAtTick {job.Segment.StartTick} \"spec_player {player}; mirv_streams record start\"");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"mirv_cmd addAtTick {job.Segment.EndTick} \"mirv_streams record end; demo_pause\"");
        cfg.AppendLine(CultureInfo.InvariantCulture, $"demo_gototick {job.Segment.StartTick}");
        await File.WriteAllTextAsync(path, cfg.ToString(), new UTF8Encoding(false), cancellationToken);
        return new GeneratedRenderScript(path, job.Video.Width, job.Video.Height,
        [
            "playdemo, demo_gototick, and spec_player must be manually checked against the installed CS2 build.",
            "The HLAE custom-loader CLI is source-confirmed; demo loading, seeking, and POV commands still require an installed-build E2E check."
        ]);
    }

    public static string EscapeCfg(string value)
    {
        if (value.Any(character => character is '\r' or '\n' or ';' or '\0'))
        {
            throw new ArgumentException("CFG value contains a forbidden control character.", nameof(value));
        }

        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
