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
        await File.WriteAllTextAsync(path, cfg.ToString(), new UTF8Encoding(false), cancellationToken);
        return new GeneratedRenderScript(path, job.Video.Width, job.Video.Height,
        [
            "POV selection and tick seeking are controlled through local CS2 NetCon and require installed-build E2E verification."
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
