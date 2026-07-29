using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

public sealed record DynamicEffectPlanningContext
{
    public required string GenerationId { get; init; }
    public required GenerationHighlight Highlight { get; init; }
    public required int TickRate { get; init; }
    public required MovieStyle Style { get; init; }
    public required EffectIntensity Intensity { get; init; }
    public MusicEditSegment? EditSegment { get; init; }
    public IReadOnlySet<string> EnabledGroups { get; init; } =
        DynamicEffectGroups.All;
    public FfmpegCapabilities? Capabilities { get; init; }
}

public static class DynamicEffectGroups
{
    public const string SmoothZooms = "smoothZooms";
    public const string PunchZooms = "punchZooms";
    public const string MotionBlur = "motionBlur";
    public const string RgbSplit = "rgbSplit";
    public const string CameraShake = "cameraShake";
    public const string HitStop = "hitStop";
    public const string FrameEcho = "frameEcho";
    public const string LensDistortion = "lensDistortion";
    public const string DynamicTransitions = "dynamicTransitions";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(
            [
                SmoothZooms,
                PunchZooms,
                MotionBlur,
                RgbSplit,
                CameraShake,
                HitStop,
                FrameEcho,
                LensDistortion,
                DynamicTransitions
            ],
            StringComparer.Ordinal);
}

public interface IDynamicEffectPlanner
{
    DynamicEffectPlan Build(DynamicEffectPlanningContext context);
}

public sealed class DynamicEffectPlanner(
    IEffectSeedProvider seedProvider,
    IEffectCompatibilityPolicy compatibility,
    IEffectBudgetPolicy budget,
    IEffectVarietyPolicy variety,
    IEffectTimeMapper timeMapper) : IDynamicEffectPlanner
{
    public const string SchemaVersion = "1.0";
    public const string PlannerVersion = "7.0";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public DynamicEffectPlan Build(DynamicEffectPlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int tickRate = Math.Max(1, context.TickRate);
        GenerationHighlight highlight = context.Highlight;
        KillDescriptor[] kills = Deserialize<KillDescriptor[]>(
                highlight.KillsJson,
                [])
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.EventIndex)
            .ToArray();
        TimeWarpPlan warp = context.EditSegment?.TimeWarp ??
            IdentityWarp(Math.Max(
                0.001,
                highlight.EstimatedDurationMilliseconds / 1000d));
        double clipDuration = TimeWarpMath.OutputDuration(
            warp,
            Math.Max(0.001, highlight.EstimatedDurationMilliseconds / 1000d));
        int planSeed = seedProvider.CreateSeed(
            context.GenerationId,
            highlight.HighlightId,
            -1,
            PlannerVersion);
        List<EffectCue> accepted = [];
        List<RejectedEffectCue> rejected = [];
        List<EffectSelectionScore> scores = [];
        List<string> warnings = [];
        if (context.Capabilities?.Available == false)
            warnings.Add("EFFECT_CAPABILITY_SCAN_UNAVAILABLE");

        for (int index = 0; index < kills.Length; index++)
        {
            KillDescriptor kill = kills[index];
            string killId = $"kill-{kill.EventIndex:D3}";
            int seed = seedProvider.CreateSeed(
                context.GenerationId,
                highlight.HighlightId,
                kill.EventIndex,
                PlannerVersion);
            DeterministicEffectRandom random = new(seed);
            double sourceKill = Math.Max(
                0,
                (kill.Tick - highlight.StartTick) / (double)tickRate);
            double outputKill = timeMapper.Map(
                sourceKill,
                sourceKill + 1d / Math.Max(240, tickRate),
                warp).ProcessedStartSeconds;
            bool finalKill = index == kills.Length - 1;
            MusicalAnchor? anchor = finalKill
                ? context.EditSegment?.TargetMusicAnchor
                : null;

            Candidate[] primaries = PrimaryCandidates(
                context,
                kill,
                index,
                kills.Length,
                anchor,
                outputKill,
                seed,
                random);
            Candidate? primary = Select(
                primaries,
                accepted,
                context,
                killId,
                scores,
                rejected,
                warnings);
            if (primary is not null)
            {
                TryAccept(
                    primary,
                    accepted,
                    rejected,
                    context,
                    clipDuration);
            }

            Candidate[] accents = AccentCandidates(
                context,
                kill,
                index,
                kills.Length,
                anchor,
                outputKill,
                seed,
                random);
            Candidate? accent = Select(
                accents,
                accepted,
                context,
                killId,
                scores,
                rejected,
                warnings);
            if (accent is not null)
            {
                TryAccept(
                    accent,
                    accepted,
                    rejected,
                    context,
                    clipDuration);
            }
        }

        Candidate? transition = CreateTransition(
            context,
            clipDuration,
            planSeed);
        if (transition is not null)
        {
            TryAccept(
                transition,
                accepted,
                rejected,
                context,
                clipDuration);
        }

        return new DynamicEffectPlan
        {
            SchemaVersion = SchemaVersion,
            PlannerVersion = PlannerVersion,
            GenerationId = context.GenerationId,
            HighlightId = highlight.HighlightId,
            ClipId = highlight.HighlightId,
            Style = context.Style,
            Intensity = context.Intensity,
            DeterministicSeed = planSeed,
            Effects = accepted
                .OrderBy(value => value.StartSeconds)
                .ThenByDescending(value => value.Priority)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray(),
            RejectedEffects = rejected,
            Warnings = warnings,
            Scores = scores
        };
    }

    private static Candidate[] PrimaryCandidates(
        DynamicEffectPlanningContext context,
        KillDescriptor kill,
        int killIndex,
        int killCount,
        MusicalAnchor? anchor,
        double killTime,
        int seed,
        DeterministicEffectRandom random)
    {
        bool finalKill = killIndex == killCount - 1;
        bool specialWeapon = kill.WeaponCode is "knife" or "taser";
        bool highImportance = context.Highlight.BeautyScore >= 55 ||
            context.Highlight.Type is nameof(HighlightType.Ace) or
                nameof(HighlightType.QuadKill);
        List<Candidate> values = [];
        if (Allowed(context, VideoEffectType.SmoothZoom))
        {
            values.Add(Zoom(
                VideoEffectType.SmoothZoom,
                EffectRole.Primary,
                killTime,
                seed,
                random,
                context,
                kill,
                anchor,
                45,
                0.16,
                0.42));
        }
        if (Allowed(context, VideoEffectType.PunchZoom))
        {
            values.Add(Zoom(
                VideoEffectType.PunchZoom,
                EffectRole.Primary,
                killTime,
                seed + 1,
                random,
                context,
                kill,
                anchor,
                kill.Headshot || kill.OneTap == true ? 82 : finalKill ? 68 : 52,
                0.10,
                0.28));
        }
        if (Allowed(context, VideoEffectType.CrashZoom) &&
            finalKill &&
            (specialWeapon || highImportance ||
             anchor?.Type == MusicalAnchorType.Drop))
        {
            values.Add(Zoom(
                VideoEffectType.CrashZoom,
                EffectRole.Primary,
                killTime,
                seed + 2,
                random,
                context,
                kill,
                anchor,
                context.Highlight.Type == nameof(HighlightType.Ace) ? 130 : 92,
                0.08,
                0.30));
        }
        if (Allowed(context, VideoEffectType.HitStop) &&
            finalKill &&
            (specialWeapon || kill.OneTap == true ||
             highImportance || anchor?.Type is
                 MusicalAnchorType.Drop or MusicalAnchorType.Downbeat))
        {
            double frames = context.Intensity switch
            {
                EffectIntensity.Minimal => 2,
                EffectIntensity.Balanced => 4,
                _ => 6
            };
            values.Add(CandidateFor(
                VideoEffectType.HitStop,
                EffectRole.Primary,
                killTime - 0.01,
                killTime + frames / 60,
                Strength(context.Intensity, 0.45, 0.65, 0.82),
                88,
                seed + 3,
                kill,
                anchor,
                new Dictionary<string, double> { ["frames"] = frames },
                EffectRenderCost.Medium,
                "IMPORTANT_KILL_TIME_ACCENT"));
        }
        return values.ToArray();
    }

    private static Candidate[] AccentCandidates(
        DynamicEffectPlanningContext context,
        KillDescriptor kill,
        int killIndex,
        int killCount,
        MusicalAnchor? anchor,
        double killTime,
        int seed,
        DeterministicEffectRandom random)
    {
        bool finalKill = killIndex == killCount - 1;
        List<Candidate> values = [];
        VideoEffectType shake = kill.WeaponCode is
            "awp" or "deagle" or "ssg08" ||
            kill.WeaponCode == "ak47" && kill.OneTap == true
                ? VideoEffectType.RecoilShake
                : VideoEffectType.MicroShake;
        if (Allowed(context, shake))
        {
            double amplitude = context.Intensity switch
            {
                EffectIntensity.Minimal => 2,
                EffectIntensity.Balanced => 4,
                _ => 7
            };
            values.Add(CandidateFor(
                shake,
                EffectRole.Accent,
                killTime - 0.02,
                killTime + random.Next(0.12, 0.23),
                Strength(context.Intensity, 0.30, 0.52, 0.74),
                kill.Headshot ? 72 : 54,
                seed + 10,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["amplitudePixels"] = amplitude * WeaponShakeMultiplier(kill.WeaponCode),
                    ["impulses"] = random.Next(2, 6)
                },
                EffectRenderCost.Low,
                "WEAPON_IMPACT"));
        }
        if (Allowed(context, VideoEffectType.RgbSplit) &&
            (kill.Headshot || kill.WeaponCode is "knife" or "taser" ||
             anchor?.Type is MusicalAnchorType.StrongBeat or MusicalAnchorType.Drop))
        {
            double offset = context.Intensity switch
            {
                EffectIntensity.Minimal => random.Next(1, 3),
                EffectIntensity.Balanced => random.Next(2, 5),
                _ => random.Next(4, 8)
            };
            values.Add(CandidateFor(
                VideoEffectType.RgbSplit,
                EffectRole.Accent,
                killTime - 0.02,
                killTime + random.Next(0.07, 0.16),
                Strength(context.Intensity, 0.24, 0.44, 0.68),
                78,
                seed + 11,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["redOffsetX"] = offset,
                    ["blueOffsetX"] = -offset
                },
                EffectRenderCost.Medium,
                "HEADSHOT_OR_MUSIC_ACCENT"));
        }
        if (Allowed(context, VideoEffectType.FrameEcho) &&
            finalKill &&
            (kill.Wallbang == true ||
             context.Highlight.Type is nameof(HighlightType.QuadKill) or
                 nameof(HighlightType.Ace) ||
             anchor?.Type == MusicalAnchorType.Drop))
        {
            values.Add(CandidateFor(
                VideoEffectType.FrameEcho,
                EffectRole.Accent,
                killTime,
                killTime + random.Next(0.10, 0.20),
                Strength(context.Intensity, 0.22, 0.42, 0.66),
                75,
                seed + 12,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["frames"] = random.Next(2, 6),
                    ["opacity"] = random.Next(0.18, 0.30)
                },
                EffectRenderCost.Medium,
                "TEMPORAL_ACCENT"));
        }
        if (Allowed(context, VideoEffectType.FlashAccent) &&
            (kill.Headshot || anchor?.Type is
                MusicalAnchorType.StrongBeat or MusicalAnchorType.Drop))
        {
            values.Add(CandidateFor(
                VideoEffectType.FlashAccent,
                EffectRole.Accent,
                killTime - 0.01,
                killTime + random.Next(0.04, 0.09),
                Strength(context.Intensity, 0.18, 0.30, 0.42),
                62,
                seed + 13,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["opacity"] = Strength(context.Intensity, 0.12, 0.20, 0.30)
                },
                EffectRenderCost.Low,
                "FLASH_ACCENT"));
        }
        if (Allowed(context, VideoEffectType.VignettePulse))
        {
            values.Add(CandidateFor(
                VideoEffectType.VignettePulse,
                EffectRole.Accent,
                killTime,
                killTime + 0.22,
                Strength(context.Intensity, 0.20, 0.32, 0.45),
                35,
                seed + 14,
                kill,
                anchor,
                new Dictionary<string, double>(),
                EffectRenderCost.Low,
                "READABILITY_SAFE_FALLBACK"));
        }
        if (Allowed(context, VideoEffectType.LensWarpPulse) &&
            finalKill &&
            (kill.WeaponCode is "knife" or "taser" ||
             context.Highlight.Type == nameof(HighlightType.Ace) ||
             anchor?.Type == MusicalAnchorType.Drop))
        {
            values.Add(CandidateFor(
                VideoEffectType.LensWarpPulse,
                EffectRole.Accent,
                killTime - 0.03,
                killTime + random.Next(0.14, 0.26),
                Strength(context.Intensity, 0.18, 0.36, 0.56),
                74,
                seed + 15,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["k1"] = -Strength(context.Intensity, 0.03, 0.06, 0.09)
                },
                EffectRenderCost.High,
                "SPECIAL_KILL_DISTORTION"));
        }
        if (Allowed(context, VideoEffectType.RollBurst) &&
            finalKill &&
            (kill.WeaponCode is "knife" or "taser" ||
             context.Highlight.TagsJson.Contains(
                 "WEAPON_SWAP",
                 StringComparison.OrdinalIgnoreCase)))
        {
            double angle = context.Intensity switch
            {
                EffectIntensity.Minimal => 0.5,
                EffectIntensity.Balanced => 1.2,
                _ => 2
            };
            values.Add(CandidateFor(
                VideoEffectType.RollBurst,
                EffectRole.Accent,
                killTime - 0.02,
                killTime + random.Next(0.16, 0.28),
                Strength(context.Intensity, 0.20, 0.42, 0.64),
                70,
                seed + 16,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["angleDegrees"] = random.NextUnit() < 0.5 ? -angle : angle
                },
                EffectRenderCost.Low,
                "SPECIAL_WEAPON_ROLL"));
        }
        if (Allowed(context, VideoEffectType.DirectionalMotionBlur) &&
            (finalKill || kill.Headshot) &&
            context.Intensity != EffectIntensity.Minimal)
        {
            values.Add(CandidateFor(
                VideoEffectType.DirectionalMotionBlur,
                EffectRole.Accent,
                killTime - 0.04,
                killTime + random.Next(0.06, 0.13),
                Strength(context.Intensity, 0.20, 0.40, 0.62),
                58,
                seed + 17,
                kill,
                anchor,
                new Dictionary<string, double>
                {
                    ["frames"] = random.Next(3, 9)
                },
                EffectRenderCost.High,
                "ZOOM_MOTION_ACCENT"));
        }
        return values.ToArray();
    }

    private Candidate? Select(
        IEnumerable<Candidate> candidates,
        IReadOnlyList<EffectCue> history,
        DynamicEffectPlanningContext context,
        string killId,
        List<EffectSelectionScore> scores,
        List<RejectedEffectCue> rejected,
        List<string> warnings)
    {
        Candidate[] materialized = candidates.ToArray();
        Candidate[] unsupported = materialized
            .Where(value => !Capable(context.Capabilities, value.Cue.Type))
            .ToArray();
        foreach (Candidate value in unsupported)
        {
            rejected.Add(new RejectedEffectCue(
                value.Cue.Type,
                "UNSUPPORTED_BY_FFMPEG_BUILD",
                value.Cue.SourceKillEventId));
        }
        Candidate[] available = materialized
            .Where(value => Capable(context.Capabilities, value.Cue.Type))
            .Select(value =>
            {
                Dictionary<string, double> breakdown =
                    new(value.Breakdown, StringComparer.Ordinal)
                    {
                        ["varietyPenalty"] = -variety.Penalty(
                            value.Cue.Type,
                            value.Cue.Category,
                            history)
                    };
                if (variety.ExceedsConsecutiveLimit(value.Cue.Type, history))
                    breakdown["consecutivePenalty"] = -1000;
                double total = breakdown.Values.Sum();
                scores.Add(new EffectSelectionScore(
                    killId,
                    value.Cue.Type,
                    total,
                    breakdown));
                return (Candidate: value, Score: total);
            })
            .Where(value => value.Score > 0)
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Candidate.Cue.Seed)
            .ThenBy(value => value.Candidate.Cue.Type)
            .Select(value => value.Candidate)
            .ToArray();
        Candidate? selected = available.FirstOrDefault();
        if (unsupported.Length > 0 && selected is not null)
        {
            VideoEffectType unavailable = unsupported
                .OrderByDescending(value => value.Breakdown.Values.Sum())
                .ThenBy(value => value.Cue.Type)
                .First()
                .Cue.Type;
            warnings.Add(
                $"{unavailable.ToString().ToUpperInvariant()}_FALLBACK_TO_{selected.Cue.Type.ToString().ToUpperInvariant()}");
        }
        return selected;
    }

    private void TryAccept(
        Candidate candidate,
        List<EffectCue> accepted,
        List<RejectedEffectCue> rejected,
        DynamicEffectPlanningContext context,
        double clipDuration)
    {
        EffectCue bounded = candidate.Cue with
        {
            StartSeconds = Math.Clamp(candidate.Cue.StartSeconds, 0, clipDuration),
            EndSeconds = Math.Clamp(candidate.Cue.EndSeconds, 0, clipDuration),
            Intensity = Math.Clamp(candidate.Cue.Intensity, 0, 1)
        };
        if (bounded.EndSeconds - bounded.StartSeconds < 1d / 240)
        {
            rejected.Add(new RejectedEffectCue(
                bounded.Type,
                "INSUFFICIENT_CLIP_DURATION",
                bounded.SourceKillEventId));
            return;
        }
        string? budgetReason = budget.Validate(bounded, accepted, clipDuration);
        if (budgetReason is not null)
        {
            rejected.Add(new RejectedEffectCue(
                bounded.Type,
                budgetReason,
                bounded.SourceKillEventId));
            return;
        }
        EffectCompatibilityResult result = compatibility.Evaluate(
            new PlannedEffect(bounded),
            accepted.Select(value => new PlannedEffect(value)).ToArray());
        if (!result.Allowed)
        {
            rejected.Add(new RejectedEffectCue(
                bounded.Type,
                result.RejectionReason ?? "EFFECT_CONFLICT_UNRESOLVED",
                bounded.SourceKillEventId));
            return;
        }
        accepted.Add(bounded with
        {
            Intensity = Math.Clamp(
                bounded.Intensity * result.IntensityMultiplier,
                0,
                1)
        });
    }

    private static Candidate Zoom(
        VideoEffectType type,
        EffectRole role,
        double killTime,
        int seed,
        DeterministicEffectRandom random,
        DynamicEffectPlanningContext context,
        KillDescriptor kill,
        MusicalAnchor? anchor,
        double score,
        double pre,
        double post)
    {
        ZoomVariant variant = SelectZoomVariant(random, context, type);
        (double minimum, double maximum) = ZoomRange(type, context.Intensity);
        double scale = random.Next(minimum, maximum);
        (double x, double y) = ZoomCenter(variant);
        return CandidateFor(
            type,
            role,
            killTime - pre,
            killTime + post,
            Strength(context.Intensity, 0.36, 0.58, 0.80),
            score,
            seed,
            kill,
            anchor,
            new Dictionary<string, double>
            {
                ["scale"] = Math.Clamp(scale, 1, 1.15),
                ["centerX"] = x,
                ["centerY"] = y,
                ["variant"] = (int)variant,
                ["peakOffsetSeconds"] = pre + 0.02
            },
            EffectRenderCost.Low,
            $"{type.ToString().ToUpperInvariant()}_SELECTED");
    }

    private static Candidate CandidateFor(
        VideoEffectType type,
        EffectRole role,
        double start,
        double end,
        double intensity,
        double score,
        int seed,
        KillDescriptor kill,
        MusicalAnchor? anchor,
        IReadOnlyDictionary<string, double> parameters,
        EffectRenderCost cost,
        string reason)
    {
        Dictionary<string, double> breakdown = new(StringComparer.Ordinal)
        {
            ["base"] = score,
            ["headshot"] = kill.Headshot ? 12 : 0,
            ["oneTap"] = kill.OneTap == true ? 10 : 0,
            ["specialWeapon"] = kill.WeaponCode is "knife" or "taser" ? 16 : 0,
            ["musicAnchor"] = anchor?.Type switch
            {
                MusicalAnchorType.Drop => 18,
                MusicalAnchorType.Downbeat => 12,
                MusicalAnchorType.StrongBeat => 10,
                _ => 0
            }
        };
        return new Candidate(
            new EffectCue
            {
                Id = $"effect-{kill.EventIndex:D3}-{type.ToString().ToLowerInvariant()}",
                Type = type,
                Category = Category(type),
                Role = role,
                StartSeconds = start,
                EndSeconds = end,
                Intensity = intensity,
                Priority = (int)Math.Round(score),
                Seed = seed,
                Parameters = parameters,
                SourceKillEventId = $"kill-{kill.EventIndex:D3}",
                SourceMusicalAnchorId = anchor?.Id,
                Reason = reason,
                RenderCost = cost
            },
            breakdown);
    }

    private static Candidate? CreateTransition(
        DynamicEffectPlanningContext context,
        double duration,
        int seed)
    {
        if (!context.EnabledGroups.Contains(DynamicEffectGroups.DynamicTransitions) ||
            duration < 0.5)
            return null;
        VideoEffectType type = context.Style switch
        {
            MovieStyle.Clean => VideoEffectType.HardCut,
            MovieStyle.Cinematic => VideoEffectType.FadeTransition,
            MovieStyle.Aggressive => seed % 3 == 0
                ? VideoEffectType.WhipZoom
                : VideoEffectType.WhipPan,
            _ => seed % 4 == 0
                ? VideoEffectType.FlashCut
                : VideoEffectType.HardCut
        };
        if (!Allowed(context, type))
            return null;
        double transitionDuration = type switch
        {
            VideoEffectType.HardCut => 1d / 60,
            VideoEffectType.FlashCut => 0.08,
            _ => 0.20
        };
        return new Candidate(
            new EffectCue
            {
                Id = $"transition-{type.ToString().ToLowerInvariant()}",
                Type = type,
                Category = VideoEffectCategory.Transition,
                Role = EffectRole.Transition,
                StartSeconds = Math.Max(0, duration - transitionDuration),
                EndSeconds = duration,
                Intensity = Strength(context.Intensity, 0.25, 0.45, 0.65),
                Priority = 20,
                Seed = seed,
                Parameters = new Dictionary<string, double>
                {
                    ["direction"] = seed % 2 == 0 ? -1 : 1
                },
                Reason = "STYLE_TRANSITION",
                RenderCost = type is
                    VideoEffectType.WhipPan or VideoEffectType.WhipZoom
                        ? EffectRenderCost.Medium
                        : EffectRenderCost.Low
            },
            new Dictionary<string, double> { ["style"] = 20 });
    }

    private static bool Allowed(
        DynamicEffectPlanningContext context,
        VideoEffectType type)
    {
        string? group = Group(type);
        if (group is not null && !context.EnabledGroups.Contains(group))
            return false;
        return context.Style switch
        {
            MovieStyle.Clean => type is
                VideoEffectType.SmoothZoom or
                VideoEffectType.ZoomPulse or
                VideoEffectType.MicroShake or
                VideoEffectType.VignettePulse or
                VideoEffectType.HardCut or
                VideoEffectType.FadeTransition,
            MovieStyle.Cinematic => type is not (
                VideoEffectType.CrashZoom or
                VideoEffectType.RecoilShake or
                VideoEffectType.FrameEcho or
                VideoEffectType.RollBurst or
                VideoEffectType.FlashCut or
                VideoEffectType.WhipZoom),
            MovieStyle.Dynamic => type is not (
                VideoEffectType.CrashZoom or
                VideoEffectType.RollBurst or
                VideoEffectType.LensWarpPulse or
                VideoEffectType.WhipZoom),
            _ => true
        };
    }

    private static bool Capable(
        FfmpegCapabilities? capabilities,
        VideoEffectType type)
    {
        if (capabilities is null || !capabilities.Available)
            return true;
        return Requirements(type).RequiredFilters.All(capabilities.Supports);
    }

    public static EffectCapabilityRequirement Requirements(VideoEffectType type) =>
        type switch
        {
            VideoEffectType.SmoothZoom or
            VideoEffectType.PunchZoom or
            VideoEffectType.CrashZoom or
            VideoEffectType.ZoomPulse or
            VideoEffectType.OffsetZoom =>
                new(["scale", "crop"], []),
            VideoEffectType.MicroShake or
            VideoEffectType.RecoilShake =>
                new(["crop"], []),
            VideoEffectType.DirectionalMotionBlur or
            VideoEffectType.ZoomBlur or
            VideoEffectType.FrameEcho =>
                new(["tmix"], ["gblur"]),
            VideoEffectType.RgbSplit =>
                new(["rgbashift"], []),
            VideoEffectType.HitStop =>
                new(["trim", "tpad", "concat"], []),
            VideoEffectType.LensWarpPulse =>
                new(["lenscorrection"], []),
            VideoEffectType.RollBurst =>
                new(["rotate"], []),
            VideoEffectType.FlashAccent =>
                new(["eq"], []),
            VideoEffectType.VignettePulse =>
                new(["vignette"], []),
            VideoEffectType.FadeTransition =>
                new(["xfade"], []),
            VideoEffectType.FlashCut =>
                new(["eq"], ["xfade"]),
            VideoEffectType.WhipPan or VideoEffectType.WhipZoom =>
                new(["scale", "crop", "gblur"], ["xfade"]),
            _ => new([], [])
        };

    private static string? Group(VideoEffectType type) => type switch
    {
        VideoEffectType.SmoothZoom or
        VideoEffectType.ZoomPulse or
        VideoEffectType.OffsetZoom => DynamicEffectGroups.SmoothZooms,
        VideoEffectType.PunchZoom or
        VideoEffectType.CrashZoom => DynamicEffectGroups.PunchZooms,
        VideoEffectType.DirectionalMotionBlur or
        VideoEffectType.ZoomBlur => DynamicEffectGroups.MotionBlur,
        VideoEffectType.RgbSplit => DynamicEffectGroups.RgbSplit,
        VideoEffectType.MicroShake or
        VideoEffectType.RecoilShake or
        VideoEffectType.RollBurst => DynamicEffectGroups.CameraShake,
        VideoEffectType.HitStop => DynamicEffectGroups.HitStop,
        VideoEffectType.FrameEcho => DynamicEffectGroups.FrameEcho,
        VideoEffectType.LensWarpPulse => DynamicEffectGroups.LensDistortion,
        VideoEffectType.HardCut or
        VideoEffectType.FadeTransition or
        VideoEffectType.FlashCut or
        VideoEffectType.WhipPan or
        VideoEffectType.WhipZoom => DynamicEffectGroups.DynamicTransitions,
        _ => null
    };

    private static VideoEffectCategory Category(VideoEffectType type) => type switch
    {
        VideoEffectType.SmoothZoom or
        VideoEffectType.PunchZoom or
        VideoEffectType.CrashZoom or
        VideoEffectType.ZoomPulse or
        VideoEffectType.OffsetZoom => VideoEffectCategory.Zoom,
        VideoEffectType.MicroShake or
        VideoEffectType.RecoilShake or
        VideoEffectType.RollBurst => VideoEffectCategory.Motion,
        VideoEffectType.DirectionalMotionBlur or
        VideoEffectType.ZoomBlur => VideoEffectCategory.Blur,
        VideoEffectType.LensWarpPulse => VideoEffectCategory.Distortion,
        VideoEffectType.FrameEcho => VideoEffectCategory.Temporal,
        VideoEffectType.RgbSplit => VideoEffectCategory.Color,
        VideoEffectType.HitStop or
        VideoEffectType.SpeedRamp => VideoEffectCategory.Time,
        VideoEffectType.HardCut or
        VideoEffectType.FadeTransition or
        VideoEffectType.FlashCut or
        VideoEffectType.WhipPan or
        VideoEffectType.WhipZoom => VideoEffectCategory.Transition,
        _ => VideoEffectCategory.Accent
    };

    private static ZoomVariant SelectZoomVariant(
        DeterministicEffectRandom random,
        DynamicEffectPlanningContext context,
        VideoEffectType type)
    {
        if (type == VideoEffectType.CrashZoom)
            return ZoomVariant.Center;
        ZoomVariant[] allowed = context.Style == MovieStyle.Cinematic
            ? [ZoomVariant.LeftBias, ZoomVariant.RightBias, ZoomVariant.UpperBias]
            : [
                ZoomVariant.Center,
                ZoomVariant.LeftBias,
                ZoomVariant.RightBias,
                ZoomVariant.UpperBias,
                ZoomVariant.LowerBias,
                ZoomVariant.Pulse
            ];
        return allowed[random.Next(0, allowed.Length)];
    }

    private static (double Minimum, double Maximum) ZoomRange(
        VideoEffectType type,
        EffectIntensity intensity)
    {
        if (type == VideoEffectType.SmoothZoom)
        {
            return intensity switch
            {
                EffectIntensity.Minimal => (1.025, 1.045),
                EffectIntensity.Balanced => (1.04, 1.07),
                _ => (1.06, 1.08)
            };
        }
        if (type == VideoEffectType.CrashZoom)
            return intensity == EffectIntensity.Strong ? (1.12, 1.15) : (1.09, 1.13);
        return intensity switch
        {
            EffectIntensity.Minimal => (1.04, 1.06),
            EffectIntensity.Balanced => (1.06, 1.10),
            _ => (1.09, 1.15)
        };
    }

    private static (double X, double Y) ZoomCenter(ZoomVariant variant) => variant switch
    {
        ZoomVariant.LeftBias => (0.46, 0.50),
        ZoomVariant.RightBias => (0.54, 0.50),
        ZoomVariant.UpperBias => (0.50, 0.46),
        ZoomVariant.LowerBias => (0.50, 0.54),
        _ => (0.50, 0.50)
    };

    private static double WeaponShakeMultiplier(string weapon) => weapon switch
    {
        "awp" => 1.25,
        "deagle" => 1.15,
        "ssg08" => 1.10,
        "ak47" => 1.0,
        _ => 0.85
    };

    private static double Strength(
        EffectIntensity intensity,
        double minimal,
        double balanced,
        double strong) => intensity switch
        {
            EffectIntensity.Minimal => minimal,
            EffectIntensity.Balanced => balanced,
            _ => strong
        };

    private static TimeWarpPlan IdentityWarp(double duration) =>
        new(
            1,
            [new TimeWarpSegment(0, duration, 1)],
            false,
            []);

    private static T Deserialize<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private sealed record Candidate(
        EffectCue Cue,
        IReadOnlyDictionary<string, double> Breakdown);
}
