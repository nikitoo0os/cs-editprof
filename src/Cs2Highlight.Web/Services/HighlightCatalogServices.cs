using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed class RecommendedSelectionOptions
{
    public int MaximumHighlights { get; set; } = 5;
    public int MaximumSoloKills { get; set; } = 2;
    public double MinimumSoloBeautyScore { get; set; } = 35;
    public double MinimumMultikillScore { get; set; } = 20;
    public bool PreferCategoryDiversity { get; set; } = true;
}

public sealed class HighlightSelectionService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    RecommendedSelectionOptions options,
    IEffectPlanner effectPlanner,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task PrepareRecommendationsAsync(
        string publicId,
        string steamId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations
            .Include(value => value.Highlights)
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        GenerationHighlight[] eligible = generation.Highlights
            .Where(value => value.SteamId == steamId)
            .ToArray();
        foreach (GenerationHighlight highlight in eligible) highlight.Recommended = false;

        IEnumerable<GenerationHighlight> ranked = eligible
            .Where(value =>
                (value.Type == HighlightType.SoloKill.ToString() &&
                 value.BeautyScore >= options.MinimumSoloBeautyScore) ||
                (value.Type != HighlightType.SoloKill.ToString() &&
                 value.TotalScore >= options.MinimumMultikillScore))
            .OrderBy(value => TypeRank(value.Type))
            .ThenByDescending(value => value.TotalScore)
            .ThenBy(value => value.RoundNumber)
            .ThenBy(value => value.FirstKillTick)
            .ThenBy(value => value.HighlightId, StringComparer.Ordinal);
        int soloCount = 0;
        int selected = 0;
        HashSet<string> categories = new(StringComparer.Ordinal);
        List<GenerationHighlight> chosen = [];
        foreach (GenerationHighlight highlight in ranked)
        {
            if (selected >= options.MaximumHighlights) break;
            if (highlight.Type == HighlightType.SoloKill.ToString() &&
                soloCount >= options.MaximumSoloKills) continue;
            if (options.PreferCategoryDiversity &&
                selected < Math.Min(options.MaximumHighlights, 3) &&
                categories.Contains(highlight.Type) &&
                ranked.Any(value => !categories.Contains(value.Type)))
                continue;
            if (chosen.Any(value => StronglyOverlaps(value, highlight)))
                continue;
            highlight.Recommended = true;
            chosen.Add(highlight);
            categories.Add(highlight.Type);
            selected++;
            if (highlight.Type == HighlightType.SoloKill.ToString()) soloCount++;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool StronglyOverlaps(
        GenerationHighlight left,
        GenerationHighlight right)
    {
        if (left.GenerationDemoId != right.GenerationDemoId)
            return false;
        long intersection = Math.Max(
            0,
            Math.Min(left.EndTick, right.EndTick) -
            Math.Max(left.StartTick, right.StartTick));
        long shorter = Math.Min(
            left.EndTick - left.StartTick,
            right.EndTick - right.StartTick);
        return shorter > 0 && intersection / (double)shorter >= 0.7;
    }

    public async Task SaveSelectionAsync(
        string publicId,
        IEnumerable<string> highlightIds,
        EffectPreset preset,
        CancellationToken cancellationToken)
    {
        string[] ids = highlightIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("NO_HIGHLIGHTS_SELECTED");
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Generation generation = await db.Generations
            .Include(value => value.Highlights)
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation.Status != GenerationStatus.AwaitingHighlightSelection)
            throw new InvalidOperationException("GENERATION_SELECTION_LOCKED");
        GenerationHighlight[] selected = generation.Highlights
            .Where(value => ids.Contains(value.HighlightId) &&
                value.SteamId == generation.SelectedSteamId)
            .ToArray();
        if (selected.Length != ids.Length)
            throw new InvalidOperationException("INVALID_HIGHLIGHT_SELECTION");
        Dictionary<string, int> order = ids
            .Select((id, index) => (id, index: index + 1))
            .ToDictionary(value => value.id, value => value.index, StringComparer.Ordinal);
        foreach (GenerationHighlight highlight in generation.Highlights)
        {
            highlight.SelectedByUser = order.TryGetValue(
                highlight.HighlightId, out int selectionOrder);
            highlight.SelectionOrder = highlight.SelectedByUser ? selectionOrder : null;
        }
        generation.MaximumHighlights = selected.Length;
        long transitionOverlap =
            Math.Max(0, selected.Length - 1) *
            Math.Max(0, generation.TransitionDurationMilliseconds);
        generation.EstimatedDurationMilliseconds = Math.Max(
            0,
            selected.Sum(value => value.EstimatedDurationMilliseconds) -
            transitionOverlap);
        generation.EffectPreset = preset;
        Dictionary<long, int> tickRates = await db.GenerationDemos
            .Where(value => value.GenerationId == generation.Id)
            .ToDictionaryAsync(
                value => value.Id,
                value => value.TickRate ?? 64,
                cancellationToken);
        foreach (GenerationHighlight highlight in selected)
        {
            HighlightEffectPlan effectPlan = effectPlanner.Build(
                highlight,
                tickRates.GetValueOrDefault(highlight.GenerationDemoId, 64),
                preset);
            db.GenerationEffectPlans.Add(new GenerationEffectPlan
            {
                GenerationId = generation.Id,
                GenerationHighlightId = highlight.Id,
                Preset = preset,
                TimelineJson = JsonSerializer.Serialize(effectPlan.Events, JsonOptions),
                EffectPlanJson = JsonSerializer.Serialize(effectPlan, JsonOptions),
                CreatedAt = timeProvider.GetUtcNow()
            });
        }
        GenerationStateMachine.Transition(
            generation, GenerationStatus.AwaitingMusicUpload, timeProvider.GetUtcNow());
        generation.ProgressPercent = Math.Max(generation.ProgressPercent, 30);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static int TypeRank(string type) => type switch
    {
        nameof(HighlightType.Ace) => 0,
        nameof(HighlightType.QuadKill) => 1,
        nameof(HighlightType.TripleKill) => 2,
        nameof(HighlightType.DoubleKill) => 3,
        nameof(HighlightType.SoloKill) => 4,
        _ => 5
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EffectType
{
    SmoothZoom,
    ImpactShake,
    ColorPunch,
    HeadshotFlash,
    VignettePulse,
    ClipTransition
}

public sealed record EffectTimelineEvent(
    EffectType Type,
    long StartMilliseconds,
    long DurationMilliseconds,
    double Intensity,
    int SourceEventIndex = -1,
    long DemoTick = 0,
    string WeaponCode = "unknown",
    bool Headshot = false,
    long PeakOffsetMilliseconds = 0);

public sealed record HighlightEffectPlan(
    string SchemaVersion,
    EffectPreset Preset,
    IReadOnlyList<EffectTimelineEvent> Events)
{
    public string ClipId { get; init; } = string.Empty;
    public long DurationMilliseconds { get; init; }
}

public sealed class EffectPlanningOptions
{
    public double MaximumZoomScale { get; init; } = 1.12;
    public double MinimumFlashGapSeconds { get; init; } = 0.25;
    public int MaximumEffectsPerClip { get; init; } = 24;
}

public interface IEffectPlanner
{
    HighlightEffectPlan Build(
        GenerationHighlight highlight,
        int tickRate,
        EffectPreset preset);
}

public sealed class EffectPlanner(
    EffectPlanningOptions? options = null) : IEffectPlanner
{
    public const string SchemaVersion = "1.2";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly EffectPlanningOptions options = options ?? new();

    public HighlightEffectPlan Build(
        GenerationHighlight highlight,
        int tickRate,
        EffectPreset preset)
    {
        if (preset == EffectPreset.None)
            return CreatePlan(highlight, preset, []);
        if (preset == EffectPreset.Clean)
            return CreatePlan(highlight, preset, CreateTransition(highlight));
        KillDescriptor[] kills = JsonSerializer.Deserialize<KillDescriptor[]>(
            highlight.KillsJson,
            JsonOptions) ?? [];
        List<EffectTimelineEvent> events = [];
        long lastFlash = long.MinValue;
        foreach (KillDescriptor kill in kills.OrderBy(value => value.Tick))
        {
            long relative = Math.Max(
                0,
                (long)Math.Round(
                    (kill.Tick - highlight.StartTick) * 1000d / Math.Max(1, tickRate),
                    MidpointRounding.AwayFromZero));
            long zoomStart = Math.Max(0, relative - 160);
            events.Add(new EffectTimelineEvent(
                EffectType.SmoothZoom,
                zoomStart,
                420,
                options.MaximumZoomScale - 1,
                kill.EventIndex,
                kill.Tick,
                kill.WeaponCode,
                kill.Headshot,
                relative - zoomStart));
            events.Add(new EffectTimelineEvent(
                EffectType.ImpactShake,
                Math.Max(0, relative - 45),
                180,
                kill.Headshot ? 1.0 : 0.72,
                kill.EventIndex,
                kill.Tick,
                kill.WeaponCode,
                kill.Headshot,
                45));
            events.Add(new EffectTimelineEvent(
                EffectType.ColorPunch,
                Math.Max(0, relative - 30),
                170,
                kill.Headshot ? 0.22 : 0.14,
                kill.EventIndex,
                kill.Tick,
                kill.WeaponCode,
                kill.Headshot,
                30));
            events.Add(new EffectTimelineEvent(
                EffectType.VignettePulse,
                relative,
                220,
                0.20,
                kill.EventIndex,
                kill.Tick,
                kill.WeaponCode,
                kill.Headshot));
            if (kill.Headshot &&
                (lastFlash == long.MinValue ||
                 relative - lastFlash >= options.MinimumFlashGapSeconds * 1000))
            {
                events.Add(new EffectTimelineEvent(
                    EffectType.HeadshotFlash,
                    Math.Max(0, relative - 20),
                    65,
                    0.16,
                    kill.EventIndex,
                    kill.Tick,
                    kill.WeaponCode,
                    true));
                lastFlash = relative;
            }
        }
        events.AddRange(CreateTransition(highlight));
        List<EffectTimelineEvent> resolved = ResolveOverlaps(events);
        EffectTimelineEvent? transition = resolved.FirstOrDefault(value =>
            value.Type == EffectType.ClipTransition);
        List<EffectTimelineEvent> bounded = resolved
            .Where(value => value.Type != EffectType.ClipTransition)
            .Take(Math.Max(0, options.MaximumEffectsPerClip - 1))
            .ToList();
        if (transition is not null && options.MaximumEffectsPerClip > 0)
            bounded.Add(transition);
        return CreatePlan(
            highlight,
            preset,
            bounded);
    }

    private static HighlightEffectPlan CreatePlan(
        GenerationHighlight highlight,
        EffectPreset preset,
        IReadOnlyList<EffectTimelineEvent> events) =>
        new(SchemaVersion, preset, events)
        {
            ClipId = highlight.HighlightId,
            DurationMilliseconds = highlight.EstimatedDurationMilliseconds
        };

    private static EffectTimelineEvent[] CreateTransition(GenerationHighlight highlight)
    {
        const long duration = 300;
        return
        [
            new(
                EffectType.ClipTransition,
                Math.Max(0, highlight.EstimatedDurationMilliseconds - duration),
                duration,
                1)
        ];
    }

    private static List<EffectTimelineEvent> ResolveOverlaps(
        IEnumerable<EffectTimelineEvent> source)
    {
        List<EffectTimelineEvent> result = [];
        foreach (EffectTimelineEvent value in source
                     .OrderBy(item => item.StartMilliseconds)
                     .ThenBy(item => item.Type))
        {
            EffectTimelineEvent normalized = value with
            {
                Intensity = value.Type switch
                {
                    EffectType.SmoothZoom => Math.Min(0.12, Math.Max(0, value.Intensity)),
                    EffectType.ImpactShake => Math.Min(1, Math.Max(0, value.Intensity)),
                    EffectType.ColorPunch => Math.Min(0.25, Math.Max(0, value.Intensity)),
                    EffectType.HeadshotFlash => Math.Min(0.18, Math.Max(0, value.Intensity)),
                    EffectType.VignettePulse => Math.Min(0.25, Math.Max(0, value.Intensity)),
                    _ => value.Intensity
                }
            };
            if (result.Count > 0 &&
                normalized.Type == EffectType.SmoothZoom &&
                result[^1].Type == normalized.Type &&
                normalized.StartMilliseconds <
                result[^1].StartMilliseconds + result[^1].DurationMilliseconds)
            {
                EffectTimelineEvent previous = result[^1];
                long end = Math.Max(
                    previous.StartMilliseconds + previous.DurationMilliseconds,
                    normalized.StartMilliseconds + normalized.DurationMilliseconds);
                result[^1] = previous with
                {
                    DurationMilliseconds = end - previous.StartMilliseconds,
                    Intensity = Math.Max(previous.Intensity, normalized.Intensity)
                };
            }
            else
            {
                result.Add(normalized);
            }
        }
        return result;
    }
}
