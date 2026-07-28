using System.Text.RegularExpressions;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.Analysis;

public sealed class RenderJobBuildOptions
{
    public required string OutputRoot { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public double Fov { get; init; } = 90;
    public int TimeoutSeconds { get; init; } = 600;
}

public interface IRenderJobBuilder
{
    RenderJob Build(string demoPath, HighlightCandidate highlight, RenderJobBuildOptions options);
}

public sealed partial class RenderJobBuilder : IRenderJobBuilder
{
    public RenderJob Build(
        string demoPath,
        HighlightCandidate highlight,
        RenderJobBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        ArgumentNullException.ThrowIfNull(options);
        if (!ulong.TryParse(highlight.PlayerId, out _))
        {
            throw new InvalidOperationException(
                $"Highlight player {highlight.PlayerId} does not have a renderable SteamID64.");
        }

        string demoSlug = SafeSlug(Path.GetFileNameWithoutExtension(demoPath));
        string typeSlug = highlight.Type.ToString().ToLowerInvariant();
        string jobId = $"{demoSlug}-r{highlight.RoundNumber}-{typeSlug}-{highlight.FirstKillTick}";
        string outputDirectory = Path.GetFullPath(Path.Combine(options.OutputRoot, "render", jobId));
        return new RenderJob(
            jobId,
            Path.GetFullPath(demoPath),
            new PlayerSelector(highlight.PlayerId, highlight.PlayerName),
            new RenderSegment(highlight.StartTick, highlight.EndTick)
            {
                TickRate = highlight.TickRate > 0 ? highlight.TickRate : null,
                RoundStartTick = highlight.RoundStartTick,
                PrimaryKillTick = highlight.PrimaryKillTick > 0
                    ? highlight.PrimaryKillTick
                    : highlight.LastKillTick,
                LastKillTick = highlight.LastKillTick,
                SafeEndTick = highlight.SafeEndTick > 0
                    ? highlight.SafeEndTick
                    : highlight.EndTick
            },
            new VideoSettings(options.Width, options.Height, options.Fps, options.Fov),
            outputDirectory,
            options.TimeoutSeconds);
    }

    public static string SafeSlug(string value)
    {
        string slug = UnsafeSlugCharacters().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "match" : slug[..Math.Min(slug.Length, 32)];
    }

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeSlugCharacters();
}
