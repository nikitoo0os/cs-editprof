using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public interface IBrollCandidateDetector
{
    IReadOnlyList<BrollCandidate> Detect(BrollDetectionContext context);
}

public sealed class BrollCandidateDetector : IBrollCandidateDetector
{
    public IReadOnlyList<BrollCandidate> Detect(BrollDetectionContext context)
    {
        if (context.TickRate <= 0)
            throw new InvalidOperationException("INVALID_BROLL_TICK_RATE");
        GameplayTimelineFrame[] eligible = context.Frames
            .Where(value =>
                value.Player.PlayerId == context.PlayerId &&
                value.Alive &&
                !value.InFreezeTime &&
                !value.NearKillEvent &&
                !OverlapsExcluded(value.Tick, context.ExcludedIntervals) &&
                (value.MovementSpeed >= context.MinimumMovementSpeed ||
                 HasPreparationEvent(value.Events)))
            .OrderBy(value => value.Tick)
            .ToArray();
        List<GameplayTimelineFrame[]> groups = [];
        foreach (IGrouping<int, GameplayTimelineFrame> round in eligible.GroupBy(
                     value => value.RoundNumber))
        {
            List<GameplayTimelineFrame> current = [];
            foreach (GameplayTimelineFrame frame in round)
            {
                if (current.Count > 0 &&
                    frame.Tick - current[^1].Tick > context.TickRate / 2)
                {
                    groups.Add(current.ToArray());
                    current.Clear();
                }
                current.Add(frame);
            }
            if (current.Count > 0)
                groups.Add(current.ToArray());
        }
        List<BrollCandidate> result = [];
        foreach (GameplayTimelineFrame[] group in groups)
        {
            int maximumTicks = Math.Max(
                1,
                (int)Math.Round(
                    context.MaximumDurationSeconds * context.TickRate));
            for (int offset = 0; offset < group.Length;)
            {
                int end = offset;
                while (end + 1 < group.Length &&
                       group[end + 1].Tick - group[offset].Tick <= maximumTicks)
                    end++;
                GameplayTimelineFrame[] window = group[offset..(end + 1)];
                offset = end + 1;
                double duration =
                    (window[^1].Tick - window[0].Tick) /
                    (double)context.TickRate;
                if (duration < context.MinimumDurationSeconds)
                    continue;
                double movement = NormalizeMovement(
                    window.Average(value => value.MovementSpeed));
                double action = Math.Clamp(
                    window.Average(value => value.ActionDensity),
                    0,
                    1);
                bool preparation = window.Any(value =>
                    HasPreparationEvent(value.Events));
                if (movement < 0.10 &&
                    !preparation &&
                    action <= context.MaximumIdleActionDensity)
                    continue;
                BrollCandidateType type = Classify(window);
                double continuity = TrajectoryContinuity(window);
                double cinematic = Math.Clamp(
                    0.45 * movement +
                    0.25 * continuity +
                    0.20 * (preparation ? 1 : 0) +
                    0.10 * (1 - Math.Abs(action - 0.35)),
                    0,
                    1);
                GameplayInterval candidateInterval = new(
                    window[0].Tick,
                    window[^1].Tick);
                if (result.Any(value => Overlaps(
                        candidateInterval,
                        new GameplayInterval(value.StartTick, value.EndTick))))
                    continue;
                result.Add(new BrollCandidate
                {
                    Id = $"broll-{context.DemoId}-{window[0].RoundNumber:D2}-{window[0].Tick}-{type}",
                    DemoId = context.DemoId,
                    RoundNumber = window[0].RoundNumber,
                    Type = type,
                    StartTick = window[0].Tick,
                    EndTick = window[^1].Tick,
                    DurationSeconds = duration,
                    MovementScore = movement,
                    CinematicScore = cinematic,
                    ActionDensity = action,
                    Trajectory = new PlayerTrajectory(
                        window.Select(value => new PlayerTransformSample(
                            value.Tick,
                            value.Player.Position,
                            value.Player.ViewAngles)).ToArray()),
                    Tags = Tags(window, preparation)
                });
            }
        }
        return result
            .OrderByDescending(value => value.CinematicScore)
            .ThenBy(value => value.StartTick)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool OverlapsExcluded(
        long tick,
        IReadOnlyList<GameplayInterval> excluded) =>
        excluded.Any(value => tick >= value.StartTick && tick <= value.EndTick);

    private static bool Overlaps(GameplayInterval left, GameplayInterval right) =>
        left.StartTick <= right.EndTick && right.StartTick <= left.EndTick;

    private static bool HasPreparationEvent(
        IReadOnlyList<GameplayEventReference> events) =>
        events.Any(value => value.Type is
            "WeaponReload" or
            "WeaponSwitch" or
            "WeaponDraw" or
            "UtilityPreparation" or
            "UtilityThrow" or
            "ScopePreparation" or
            "BombPlant" or
            "BombDefuse");

    private static BrollCandidateType Classify(
        IReadOnlyList<GameplayTimelineFrame> frames)
    {
        string[] eventTypes = frames
            .SelectMany(value => value.Events)
            .Select(value => value.Type)
            .ToArray();
        if (eventTypes.Contains("BombPlant", StringComparer.Ordinal))
            return BrollCandidateType.BombPlant;
        if (eventTypes.Contains("BombDefuse", StringComparer.Ordinal))
            return BrollCandidateType.BombDefuse;
        if (eventTypes.Contains("UtilityThrow", StringComparer.Ordinal))
            return BrollCandidateType.UtilityThrow;
        if (eventTypes.Contains("UtilityPreparation", StringComparer.Ordinal))
            return BrollCandidateType.UtilityPreparation;
        if (eventTypes.Contains("WeaponReload", StringComparer.Ordinal))
            return BrollCandidateType.WeaponReload;
        if (eventTypes.Contains("WeaponSwitch", StringComparer.Ordinal))
            return BrollCandidateType.WeaponSwitch;
        if (eventTypes.Contains("ScopePreparation", StringComparer.Ordinal))
            return BrollCandidateType.ScopePreparation;
        return BrollCandidateType.PlayerApproach;
    }

    private static string[] Tags(
        GameplayTimelineFrame[] frames,
        bool preparation)
    {
        HashSet<string> tags = new(StringComparer.Ordinal)
        {
            "SELECTED_PLAYER",
            "NON_FREEZE_TIME",
            "ALIVE"
        };
        if (preparation)
            tags.Add("PREPARATION");
        if (frames.Average(value => value.MovementSpeed) > 150)
            tags.Add("FAST_MOVEMENT");
        return tags.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static double NormalizeMovement(double speed) =>
        Math.Clamp(speed / 250, 0, 1);

    private static double TrajectoryContinuity(
        GameplayTimelineFrame[] frames)
    {
        if (frames.Length < 3)
            return 0.5;
        List<double> distances = [];
        for (int index = 1; index < frames.Length; index++)
        {
            distances.Add(frames[index].Player.Position.DistanceTo(
                frames[index - 1].Player.Position));
        }
        double average = distances.Average();
        if (average <= 0.001)
            return 0;
        double variance = distances.Average(value =>
            Math.Pow(value - average, 2));
        return Math.Clamp(1 - Math.Sqrt(variance) / average, 0, 1);
    }
}

public interface IMapCameraProfileCatalog
{
    MapCameraProfile? Find(string mapName);
    IReadOnlyList<MapCameraProfile> All { get; }
}

public sealed class MapCameraProfileCatalog(
    IEnumerable<MapCameraProfile>? profiles = null) : IMapCameraProfileCatalog
{
    private readonly IReadOnlyDictionary<string, MapCameraProfile> profiles =
        (profiles ?? UnverifiedDefaults())
        .ToDictionary(value => value.MapName, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MapCameraProfile> All => profiles.Values
        .OrderBy(value => value.MapName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public MapCameraProfile? Find(string mapName) =>
        profiles.GetValueOrDefault(mapName);

    private static IEnumerable<MapCameraProfile> UnverifiedDefaults()
    {
        yield return new MapCameraProfile
        {
            MapName = "de_dust2",
            SafeVolumes =
            [
                new SafeCameraVolume(
                    new GameplayVector3(100, 2280, -80),
                    new GameplayVector3(250, 2450, 20))
            ],
            EstablishingShots =
            [
                new EstablishingCameraPreset(
                    "de-dust2-upper-tunnel-stage8-1",
                    [
                        new CameraKeyframe
                        {
                            TimeSeconds = 0,
                            Position = new GameplayVector3(
                                160.122742,
                                2369.676270,
                                -56.481201),
                            Rotation = new GameplayVector3(14.5, 8, 0),
                            Fov = 82
                        },
                        new CameraKeyframe
                        {
                            TimeSeconds = 1,
                            Position = new GameplayVector3(180, 2345, -50),
                            Rotation = new GameplayVector3(15, 16, 0),
                            Fov = 80
                        },
                        new CameraKeyframe
                        {
                            TimeSeconds = 2,
                            Position = new GameplayVector3(205, 2320, -44),
                            Rotation = new GameplayVector3(17, 27, 0),
                            Fov = 78
                        },
                        new CameraKeyframe
                        {
                            TimeSeconds = 3,
                            Position = new GameplayVector3(230, 2295, -36),
                            Rotation = new GameplayVector3(19, 33, 0),
                            Fov = 76
                        }
                    ])
            ],
            RestrictedVolumes = [],
            ManuallyVerified = true
        };
        foreach (string map in new[] { "de_mirage", "de_inferno" })
        {
            yield return new MapCameraProfile
            {
                MapName = map,
                SafeVolumes = [],
                EstablishingShots = [],
                RestrictedVolumes = [],
                ManuallyVerified = false
            };
        }
    }
}

public interface ICameraPathPlanner
{
    CameraShotPlan Create(
        BrollCandidate candidate,
        CameraPlanningContext context);
}

public sealed class CameraPathPlanner : ICameraPathPlanner
{
    public CameraShotPlan Create(
        BrollCandidate candidate,
        CameraPlanningContext context)
    {
        List<string> warnings = [];
        if (!context.Capabilities.Available ||
            !context.Capabilities.SupportsCampath ||
            !context.Capabilities.SupportsInput ||
            !context.Capabilities.ManualSpikeVerified)
        {
            warnings.Add("HLAE_CAMERA_CAPABILITY_UNVERIFIED");
            return Pov(candidate, warnings);
        }
        if (context.Profile is null ||
            !context.Profile.ManuallyVerified ||
            context.Profile.SafeVolumes.Count == 0)
        {
            warnings.Add("CAMERA_MAP_PROFILE_UNSUPPORTED");
            return Pov(candidate, warnings);
        }
        PlayerTransformSample[] samples = candidate.Trajectory.Samples
            .OrderBy(value => value.Tick)
            .ToArray();
        if (samples.Length < 2)
        {
            warnings.Add("CAMERA_TRAJECTORY_INSUFFICIENT");
            return Pov(candidate, warnings);
        }
        List<CameraKeyframe> keyframes = [];
        const int count = 4;
        for (int index = 0; index < count; index++)
        {
            double position = index / (double)(count - 1);
            PlayerTransformSample sample = Sample(samples, position);
            PlayerTransformSample neighbor = Sample(
                samples,
                Math.Min(1, position + 1d / (count - 1)));
            GameplayVector3 direction = Normalize(
                neighbor.Position.X - sample.Position.X,
                neighbor.Position.Y - sample.Position.Y,
                0);
            if (Length(direction) < 0.001)
                direction = DirectionFromYaw(sample.ViewAngles.Y);
            GameplayVector3 side = new(-direction.Y, direction.X, 0);
            bool sideTracking = candidate.Type is
                BrollCandidateType.SideMovement or
                BrollCandidateType.UtilityThrow;
            GameplayVector3 offset = sideTracking
                ? Scale(side, context.CameraDistance)
                : Scale(direction, -context.CameraDistance);
            GameplayVector3 camera = Add(
                sample.Position,
                new GameplayVector3(
                    offset.X,
                    offset.Y,
                    context.CameraHeight));
            if (!context.Profile.SafeVolumes.Any(value => value.Contains(camera)))
            {
                warnings.Add("CAMERA_PATH_OUTSIDE_SAFE_VOLUME");
                return EstablishingFallback(candidate, context.Profile, warnings);
            }
            keyframes.Add(new CameraKeyframe
            {
                TimeSeconds = candidate.DurationSeconds * position,
                Position = camera,
                Rotation = sample.ViewAngles,
                Fov = Math.Clamp(
                    86 - 8 * position,
                    context.MinimumFov,
                    context.MaximumFov)
            });
        }
        if (keyframes
            .Zip(keyframes.Skip(1))
            .Any(pair => pair.First.Position.DistanceTo(pair.Second.Position) < 0.05))
        {
            warnings.Add("CAMERA_KEYFRAMES_NOT_DISTINCT");
            return Pov(candidate, warnings);
        }
        CameraShotType type = candidate.Type switch
        {
            BrollCandidateType.SideMovement => CameraShotType.SideTracking,
            BrollCandidateType.PlayerApproach => CameraShotType.RearTracking,
            _ => CameraShotType.LinearCampath
        };
        return new CameraShotPlan
        {
            Id = $"camera-{candidate.Id}",
            Type = type,
            DemoId = candidate.DemoId,
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = candidate.DurationSeconds,
            Keyframes = keyframes,
            FovStart = keyframes[0].Fov,
            FovEnd = keyframes[^1].Fov,
            RequiresHighFpsCapture = candidate.CinematicScore >= 0.8,
            FallbackShotId = $"camera-{candidate.Id}-pov",
            Warnings = warnings
        };
    }

    private static CameraShotPlan EstablishingFallback(
        BrollCandidate candidate,
        MapCameraProfile profile,
        IReadOnlyList<string> warnings)
    {
        EstablishingCameraPreset? preset =
            profile.EstablishingShots.FirstOrDefault(value =>
                value.Keyframes.Count >= 4);
        if (preset is null)
            return Pov(candidate, warnings);
        CameraKeyframe[] source = preset.Keyframes
            .OrderBy(value => value.TimeSeconds)
            .ToArray();
        double sourceDuration = Math.Max(
            0.001,
            source[^1].TimeSeconds - source[0].TimeSeconds);
        CameraKeyframe[] keyframes = source
            .Select(value => value with
            {
                TimeSeconds = candidate.DurationSeconds *
                    (value.TimeSeconds - source[0].TimeSeconds) /
                    sourceDuration
            })
            .ToArray();
        return new CameraShotPlan
        {
            Id = $"camera-{candidate.Id}-{preset.Id}",
            Type = CameraShotType.EnvironmentReveal,
            DemoId = candidate.DemoId,
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = candidate.DurationSeconds,
            Keyframes = keyframes,
            FovStart = keyframes[0].Fov,
            FovEnd = keyframes[^1].Fov,
            RequiresHighFpsCapture = false,
            FallbackShotId = $"camera-{candidate.Id}-pov",
            Warnings = warnings
        };
    }

    public static CameraShotPlan Pov(
        BrollCandidate candidate,
        IReadOnlyList<string> warnings) =>
        new()
        {
            Id = $"camera-{candidate.Id}-pov",
            Type = CameraShotType.PlayerPov,
            DemoId = candidate.DemoId,
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = candidate.DurationSeconds,
            Keyframes = [],
            FovStart = 90,
            FovEnd = 90,
            RequiresHighFpsCapture = false,
            FallbackShotId = string.Empty,
            Warnings = warnings
        };

    private static PlayerTransformSample Sample(
        PlayerTransformSample[] samples,
        double position)
    {
        int index = (int)Math.Round(
            position * (samples.Length - 1),
            MidpointRounding.AwayFromZero);
        return samples[Math.Clamp(index, 0, samples.Length - 1)];
    }

    private static GameplayVector3 DirectionFromYaw(double yaw)
    {
        double radians = yaw * Math.PI / 180;
        return new GameplayVector3(
            Math.Cos(radians),
            Math.Sin(radians),
            0);
    }

    private static GameplayVector3 Normalize(double x, double y, double z)
    {
        double length = Math.Sqrt(x * x + y * y + z * z);
        return length <= 0.000001
            ? GameplayVector3.Zero
            : new GameplayVector3(x / length, y / length, z / length);
    }

    private static double Length(GameplayVector3 value) =>
        Math.Sqrt(
            value.X * value.X +
            value.Y * value.Y +
            value.Z * value.Z);

    private static GameplayVector3 Scale(GameplayVector3 value, double scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    private static GameplayVector3 Add(GameplayVector3 left, GameplayVector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
}

public interface ICameraShotQualityAnalyzer
{
    IReadOnlyList<string> Validate(
        CameraShotPlan shot,
        CameraPreviewMetrics metrics);
}

public sealed class CameraShotQualityAnalyzer : ICameraShotQualityAnalyzer
{
    public IReadOnlyList<string> Validate(
        CameraShotPlan shot,
        CameraPreviewMetrics metrics)
    {
        List<string> warnings = [];
        if (!metrics.HasVideo || metrics.DurationSeconds <= 0)
            warnings.Add("CAMERA_PREVIEW_VIDEO_INVALID");
        if (metrics.BlackFrameRatio > 0.05 ||
            metrics.AverageBrightness < 0.03)
            warnings.Add("CAMERA_PREVIEW_BLACK_FRAMES");
        if (metrics.JumpScore > 0.40)
            warnings.Add("CAMERA_PREVIEW_ABRUPT_JUMP");
        if (metrics.StaticRatio > 0.80 &&
            shot.Type is not CameraShotType.StaticEstablishing)
            warnings.Add("CAMERA_PREVIEW_TOO_STATIC");
        if (shot.Type is not CameraShotType.PlayerPov &&
            shot.Keyframes.Count < 4)
            warnings.Add("CAMERA_CAMPATH_KEYFRAME_COUNT_INVALID");
        if (shot.Keyframes.Zip(shot.Keyframes.Skip(1)).Any(pair =>
                pair.First.TimeSeconds >= pair.Second.TimeSeconds))
            warnings.Add("CAMERA_KEYFRAME_TIME_ORDER_INVALID");
        if (shot.Keyframes.Any(value => value.Fov is < 20 or > 140))
            warnings.Add("CAMERA_FOV_INVALID");
        return warnings;
    }
}

public interface IHighlightPeakMatcher
{
    HighlightPeakMatchPlan Match(
        IReadOnlyList<SelectedHighlight> highlights,
        MusicExcerptPlan excerpt,
        HighlightPeakMatchingOptions options);
}

public sealed class HighlightPeakMatcher(
    IHighlightImportanceCalculator importanceCalculator) : IHighlightPeakMatcher
{
    public HighlightPeakMatchPlan Match(
        IReadOnlyList<SelectedHighlight> highlights,
        MusicExcerptPlan excerpt,
        HighlightPeakMatchingOptions options)
    {
        MusicalPeak[] available = excerpt.Peaks
            .Where(value =>
                value.Strength >= options.MinimumPeakStrength &&
                value.Confidence >= options.MinimumPeakConfidence)
            .OrderByDescending(PeakScore)
            .ThenBy(value => value.TimeSeconds)
            .ToArray();
        (SelectedHighlight Highlight, double Importance)[] ranked = highlights
            .Select(value => (
                value,
                importanceCalculator.Calculate(
                    value.Highlight,
                    value.SelectionOrder).Total))
            .OrderByDescending(value => value.Total)
            .ThenBy(value => value.value.SelectionOrder)
            .ThenBy(value => value.value.Id, StringComparer.Ordinal)
            .Select(value => (value.value, value.Total))
            .ToArray();
        List<HighlightPeakMatch> matches = [];
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach ((SelectedHighlight highlight, double importance) in ranked)
        {
            MusicalPeak? selected = available
                .Where(value =>
                    !used.Contains(value.Id) &&
                    matches.All(match =>
                        Math.Abs(
                            match.Peak.TimeSeconds -
                            value.TimeSeconds) >=
                        options.MinimumPeakGapSeconds))
                .OrderByDescending(value =>
                    MatchScore(highlight, importance, value))
                .ThenBy(value => value.TimeSeconds)
                .FirstOrDefault();
            if (selected is null)
                continue;
            used.Add(selected.Id);
            double planned = selected.TimeSeconds - excerpt.StartSeconds;
            matches.Add(new HighlightPeakMatch
            {
                HighlightId = highlight.Id,
                Peak = selected,
                HighlightImportance = importance,
                PlannedPeakSeconds = planned,
                PlannedKillSeconds = planned,
                AlignmentErrorMilliseconds = 0,
                Score = MatchScore(highlight, importance, selected),
                Warnings = []
            });
        }
        string[] unmatched = highlights
            .Where(value => matches.All(match => match.HighlightId != value.Id))
            .Select(value => value.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new HighlightPeakMatchPlan
        {
            Matches = matches
                .OrderBy(value => value.PlannedPeakSeconds)
                .ToArray(),
            UnmatchedHighlightIds = unmatched,
            Warnings = unmatched.Length == 0
                ? []
                : ["HIGHLIGHTS_REDUCED_FOR_AVAILABLE_PEAKS"]
        };
    }

    private static double MatchScore(
        SelectedHighlight highlight,
        double importance,
        MusicalPeak peak)
    {
        double category = highlight.Highlight.Type switch
        {
            HighlightType.Ace when peak.Type == MusicalPeakType.DropStart => 35,
            HighlightType.QuadKill when peak.Type is
                MusicalPeakType.DropStart or
                MusicalPeakType.ChorusStart => 30,
            HighlightType.TripleKill when peak.Type == MusicalPeakType.Downbeat => 24,
            HighlightType.SoloKill when peak.Type is
                MusicalPeakType.BassImpact or
                MusicalPeakType.StrongBeat => 16,
            _ => 8
        };
        return importance * peak.Strength * peak.Confidence * 20 + category;
    }

    private static double PeakScore(MusicalPeak peak) =>
        peak.Strength *
        peak.Confidence *
        (peak.Type switch
        {
            MusicalPeakType.DropStart => 1.5,
            MusicalPeakType.ChorusStart => 1.35,
            MusicalPeakType.BassImpact => 1.25,
            MusicalPeakType.Downbeat => 1.20,
            _ => 1
        });
}

public interface ICinematicTimeWarpPolicy
{
    TimeWarpPlan Create(
        SelectedHighlight highlight,
        HighlightPeakMatch match,
        double outputStartSeconds,
        CinematicTimeWarpOptions options);
}

public sealed class CinematicTimeWarpPolicy(
    ITimeWarpPlanner planner) : ICinematicTimeWarpPolicy
{
    public TimeWarpPlan Create(
        SelectedHighlight highlight,
        HighlightPeakMatch match,
        double outputStartSeconds,
        CinematicTimeWarpOptions options)
    {
        MusicalAnchor anchor = new(
            match.Peak.Id,
            MusicalAnchorType.StrongBeat,
            match.PlannedPeakSeconds,
            match.Peak.Strength,
            match.Peak.Confidence);
        TimeWarpPlan plan = planner.Create(
            highlight.Bounds,
            anchor,
            outputStartSeconds,
            MusicSyncIntensity.Expressive,
            new TimeWarpOptions
            {
                ExpressiveMinimumBaseSpeed = options.MinimumBaseSpeed,
                ExpressiveMaximumBaseSpeed = options.MaximumBaseSpeed,
                ExpressiveMinimumRampSpeed = options.MinimumLocalSpeed,
                ExpressiveMaximumRampSpeed = options.MaximumLocalSpeed,
                MaximumRampDurationSeconds = options.MaximumRampDurationSeconds
            });
        double killOffset =
            highlight.Bounds.PrimaryKillSeconds -
            highlight.Bounds.SafeStartSeconds;
        TimeWarpSegment[] safeSegments = plan.Segments
            .Select(value => value.SourceStartSeconds >= killOffset
                ? value with
                {
                    Speed = Math.Min(
                        value.Speed,
                        options.MaximumPostKillAcceleration)
                }
                : value)
            .ToArray();
        return plan with { Segments = safeSegments };
    }
}

public interface IMotivatedEffectPlanner
{
    IReadOnlyList<MotivatedEffectDirective> Plan(
        CinematicSequenceRole role,
        MusicSection section,
        HighlightPeakMatch? match,
        CameraShotPlan camera,
        double segmentDuration,
        CinematicEffectPolicy policy,
        bool finalHighlight);
}

public sealed class MotivatedEffectPlanner : IMotivatedEffectPlanner
{
    public IReadOnlyList<MotivatedEffectDirective> Plan(
        CinematicSequenceRole role,
        MusicSection section,
        HighlightPeakMatch? match,
        CameraShotPlan camera,
        double segmentDuration,
        CinematicEffectPolicy policy,
        bool finalHighlight)
    {
        if (role is not (
                CinematicSequenceRole.Highlight or
                CinematicSequenceRole.PeakHighlight))
            return [];
        if (policy.MaximumVisibleFilterEffectsPerHighlight <= 0)
            return [];
        if (policy.PreferCameraMotionOverFilterEffects &&
            camera.Type != CameraShotType.PlayerPov)
            return [];
        MotivatedEffectReason reason = finalHighlight
            ? MotivatedEffectReason.FinalKill
            : match?.Peak.Type == MusicalPeakType.BassImpact
                ? MotivatedEffectReason.BassImpact
                : MotivatedEffectReason.MusicPeak;
        string type = finalHighlight
            ? "HitStop"
            : match?.Peak.Type == MusicalPeakType.BassImpact
                ? "SmoothZoom"
                : "OffsetZoom";
        double center = Math.Clamp(
            match?.PlannedKillSeconds ?? segmentDuration / 2,
            0,
            segmentDuration);
        return
        [
            new MotivatedEffectDirective(
                type,
                reason,
                Math.Max(0, center - 0.10),
                Math.Min(segmentDuration, center + 0.12),
                finalHighlight ? 0.68 : 0.42)
        ];
    }
}

public interface ISoundDesignPlanner
{
    SoundDesignPlan Create(IReadOnlyList<MusicSection> sections);
}

public sealed class SoundDesignPlanner : ISoundDesignPlanner
{
    public SoundDesignPlan Create(IReadOnlyList<MusicSection> sections) =>
        new(
            sections.Select(value => value.Type switch
            {
                MusicSectionType.Intro or MusicSectionType.Calm or
                    MusicSectionType.Verse =>
                    new SoundDesignSection(value.Id, -20, -2, true, false),
                MusicSectionType.BuildUp or MusicSectionType.PreDrop =>
                    new SoundDesignSection(value.Id, -14, -3, true, false),
                MusicSectionType.Drop or MusicSectionType.Chorus or
                    MusicSectionType.HighEnergy =>
                    new SoundDesignSection(value.Id, -8, -3, false, true),
                _ => new SoundDesignSection(value.Id, -16, -4, false, false)
            }).ToArray(),
            PreservePostKillTail: true,
            Warnings: []);
}

public interface IColorNarrativePlanner
{
    ColorNarrativePlan Create(
        IReadOnlyList<MusicSection> sections,
        ColorGradePreset grade);
}

public sealed class ColorNarrativePlanner : IColorNarrativePlanner
{
    public ColorNarrativePlan Create(
        IReadOnlyList<MusicSection> sections,
        ColorGradePreset grade) =>
        new(
            grade,
            sections.Select(value => value.Type switch
            {
                MusicSectionType.Intro or MusicSectionType.Calm =>
                    new ColorNarrativeSection(value.Id, 0.96, -0.02),
                MusicSectionType.BuildUp or MusicSectionType.PreDrop =>
                    new ColorNarrativeSection(value.Id, 0.99, 0),
                MusicSectionType.Drop or MusicSectionType.Chorus or
                    MusicSectionType.HighEnergy =>
                    new ColorNarrativeSection(value.Id, 1, 0),
                MusicSectionType.Outro =>
                    new ColorNarrativeSection(value.Id, 0.96, -0.06),
                _ => new ColorNarrativeSection(value.Id, 0.98, -0.01)
            }).ToArray(),
            []);
}
