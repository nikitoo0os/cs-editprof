using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public static class CaptureUiProfileAdapter
{
    public const string TemplateVersion = "capture-clean.v1";

    private static readonly Lazy<string[]> GameplayCommands = new(LoadGameplayTemplate);

    public static IReadOnlyList<string> GetCommands(CaptureUiProfile profile) =>
        profile switch
        {
            CaptureUiProfile.Gameplay => GameplayCommands.Value,
            CaptureUiProfile.Minimal =>
            [
                "demo_timescale 1",
                "cl_drawhud 0",
                "spec_show_xray 0",
                "gameui_hide",
                "hideconsole"
            ],
            CaptureUiProfile.Cinematic =>
            [
                "demo_timescale 1",
                "cl_drawhud 0",
                "r_drawviewmodel 0",
                "spec_show_xray 0",
                "gameui_hide",
                "hideconsole"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };

    private static string[] LoadGameplayTemplate()
    {
        const string resource =
            "Cs2Highlight.RenderAgent.Infrastructure.Templates.capture-clean.v1.cfg.template";
        using Stream stream = typeof(CaptureUiProfileAdapter).Assembly
            .GetManifestResourceStream(resource) ??
            throw new InvalidOperationException(
                $"Embedded capture profile was not found: {resource}");
        using StreamReader reader = new(stream);
        List<string> commands = [];
        while (reader.ReadLine() is { } line)
        {
            string value = line.Trim();
            if (value.Length > 0 && !value.StartsWith("//", StringComparison.Ordinal))
                commands.Add(value);
        }
        return commands.Count > 0
            ? commands.ToArray()
            : throw new InvalidOperationException("Gameplay capture profile is empty.");
    }
}
