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
                Dictionary<string, PlayerTrajectory> subjects =
                    SceneSubjects(context, window);
                long? jumpTick = JumpFocusTick(window);
                BrollCandidateType type = jumpTick.HasValue
                    ? BrollCandidateType.PlayerJump
                    : Classify(window);
                if (!jumpTick.HasValue && subjects.Count >= 2)
                {
                    type = preparation
                        ? BrollCandidateType.TeamSetup
                        : BrollCandidateType.TeamMovement;
                }
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
                    Tags = Tags(window, preparation, subjects.Count),
                    FocusTick = jumpTick,
                    SubjectIds = subjects.Keys
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    SubjectTrajectories = subjects
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

    private static long? JumpFocusTick(
        GameplayTimelineFrame[] frames)
    {
        if (frames.Length < 3)
            return null;
        double verticalRange = frames.Max(value => value.Player.Position.Z) -
            frames.Min(value => value.Player.Position.Z);
        GameplayTimelineFrame focus = frames
            .OrderByDescending(value => Math.Abs(value.Player.Velocity.Z))
            .First();
        return verticalRange >= 18 &&
               Math.Abs(focus.Player.Velocity.Z) >= 105
            ? focus.Tick
            : null;
    }

    private static string[] Tags(
        GameplayTimelineFrame[] frames,
        bool preparation,
        int subjectCount)
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
        if (subjectCount >= 2)
            tags.Add("TEAM_GROUP");
        return tags.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, PlayerTrajectory> SceneSubjects(
        BrollDetectionContext context,
        GameplayTimelineFrame[] selectedFrames)
    {
        Dictionary<string, PlayerTrajectory> result = new(
            StringComparer.Ordinal)
        {
            [context.PlayerId] = new PlayerTrajectory(
                selectedFrames.Select(value => new PlayerTransformSample(
                    value.Tick,
                    value.Player.Position,
                    value.Player.ViewAngles)).ToArray())
        };
        string? team = selectedFrames
            .Select(value => value.Team)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (team is null)
            return result;
        long start = selectedFrames[0].Tick;
        long end = selectedFrames[^1].Tick;
        foreach (IGrouping<string, GameplayTimelineFrame> group in
                 context.Frames.Where(value =>
                         value.Player.PlayerId != context.PlayerId &&
                         value.Alive &&
                         value.RoundNumber == selectedFrames[0].RoundNumber &&
                         value.Tick >= start &&
                         value.Tick <= end &&
                         string.Equals(value.Team, team, StringComparison.Ordinal))
                     .GroupBy(value => value.Player.PlayerId))
        {
            GameplayTimelineFrame[] nearby = group
                .OrderBy(value => value.Tick)
                .Where(value =>
                {
                    GameplayTimelineFrame closest = selectedFrames
                        .OrderBy(item => Math.Abs(item.Tick - value.Tick))
                        .First();
                    return Math.Abs(closest.Tick - value.Tick) <=
                               context.TickRate / 3 &&
                           closest.Player.Position.DistanceTo(
                               value.Player.Position) <= 640;
                })
                .ToArray();
            if (nearby.Length < Math.Max(2, selectedFrames.Length / 2))
                continue;
            result[group.Key] = new PlayerTrajectory(
                nearby.Select(value => new PlayerTransformSample(
                    value.Tick,
                    value.Player.Position,
                    value.Player.ViewAngles)).ToArray());
        }
        return result;
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
    private static MapCameraProfile[] ResolveProfiles(
        IEnumerable<MapCameraProfile>? configured)
    {
        MapCameraProfile[] materialized = configured?.ToArray() ?? [];
        return materialized.Length > 0
            ? materialized
            : UnverifiedDefaults().ToArray();
    }

    private readonly IReadOnlyDictionary<string, MapCameraProfile> profiles =
        ResolveProfiles(profiles)
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
        if (candidate.DurationSeconds < 1.5)
        {
            warnings.Add("CAMERA_SHOT_TOO_SHORT_FOR_CAMPATH");
            return Pov(candidate, warnings);
        }
        if (!context.Capabilities.Available ||
            !context.Capabilities.SupportsCampath ||
            !context.Capabilities.SupportsInput ||
            !context.Capabilities.ManualSpikeVerified)
        {
            warnings.Add("HLAE_CAMERA_CAPABILITY_UNVERIFIED");
            return Pov(candidate, warnings);
        }
        if (context.Profile is null ||
            (!context.Profile.ManuallyVerified &&
             !context.Profile.AutomaticallyCalibrated) ||
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
        CameraShotFamily family = FamilyFor(candidate);
        if (family == CameraShotFamily.PlayerPov)
            return Pov(candidate, warnings);
        if (family == CameraShotFamily.StaticTripod)
        {
            return CreateStaticTripod(
                candidate,
                context.Profile,
                context,
                warnings);
        }
        GameplayVector3 startSubject = SubjectPosition(
            candidate,
            0,
            samples[0].Position);
        GameplayVector3 endSubject = context.DestinationSubjectPosition ??
            SubjectPosition(candidate, 1, samples[^1].Position);
        GameplayVector3 routeDirection = Normalize(
            endSubject.X - startSubject.X,
            endSubject.Y - startSubject.Y,
            0);
        if (Length(routeDirection) < 0.001)
            routeDirection = DirectionFromYaw(samples[0].ViewAngles.Y);
        GameplayVector3 routeSide = new(
            -routeDirection.Y,
            routeDirection.X,
            0);
        GameplayVector3 cameraA = CameraEndpoint(
            startSubject,
            routeDirection,
            routeSide,
            family,
            context,
            destination: false);
        GameplayVector3 cameraB = CameraEndpoint(
            endSubject,
            routeDirection,
            routeSide,
            family,
            context,
            destination: true);
        double maximumRouteDistance = Math.Max(
            24,
            context.MaximumCameraSpeedUnitsPerSecond *
            candidate.DurationSeconds);
        double routeDistance = cameraA.DistanceTo(cameraB);
        if (routeDistance > maximumRouteDistance)
        {
            cameraA = Lerp(
                cameraB,
                cameraA,
                maximumRouteDistance / routeDistance);
            warnings.Add("CAMERA_ROUTE_SPEED_CLAMPED");
        }
        List<CameraKeyframe> keyframes = [];
        const int count = 4;
        for (int index = 0; index < count; index++)
        {
            double timeProgress = index / (double)(count - 1);
            double routeProgress = SmoothStep(timeProgress);
            GameplayVector3 camera = Lerp(
                cameraA,
                cameraB,
                routeProgress);
            GameplayVector3 subject = Lerp(
                startSubject,
                endSubject,
                routeProgress);
            if (!context.Profile.SafeVolumes.Any(value => value.Contains(camera)))
            {
                warnings.Add("CAMERA_PATH_OUTSIDE_SAFE_VOLUME");
                return EstablishingFallback(
                    candidate,
                    context.Profile,
                    context,
                    warnings);
            }
            keyframes.Add(new CameraKeyframe
            {
                TimeSeconds = candidate.DurationSeconds * timeProgress,
                Position = camera,
                Rotation = LookAt(
                    camera,
                    Add(subject, new GameplayVector3(0, 0, 54))),
                Fov = Math.Clamp(
                    family switch
                    {
                        CameraShotFamily.GroupWide => 94,
                        CameraShotFamily.WeaponDetail => 70,
                        _ => 84 - 2 * routeProgress
                    },
                    context.MinimumFov,
                    context.MaximumFov)
            });
        }
        if (context.DestinationSubjectPosition is not null)
            warnings.Add("CAMERA_ROUTE_B_ANCHORED_TO_NEXT_HIGHLIGHT");
        if (keyframes
            .Zip(keyframes.Skip(1))
            .Any(pair => pair.First.Position.DistanceTo(pair.Second.Position) < 0.05))
        {
            warnings.Add("CAMERA_KEYFRAMES_NOT_DISTINCT");
            return Pov(candidate, warnings);
        }
        CameraShotType type = family switch
        {
            CameraShotFamily.SideTracking => CameraShotType.SideTracking,
            CameraShotFamily.RearTracking => CameraShotType.RearTracking,
            CameraShotFamily.FrontTracking => CameraShotType.FrontTracking,
            CameraShotFamily.GroupWide => CameraShotType.GroupWide,
            CameraShotFamily.Orbit => CameraShotType.Orbit,
            CameraShotFamily.WeaponDetail => CameraShotType.WeaponDetail,
            CameraShotFamily.EnvironmentReveal => CameraShotType.EnvironmentReveal,
            _ => CameraShotType.LinearCampath
        };
        SafeCameraVolume? safetyVolume =
            context.Profile.SafeVolumes.FirstOrDefault(value =>
                keyframes.All(keyframe => value.Contains(keyframe.Position)));
        if (safetyVolume is null &&
            context.Profile.AutomaticallyCalibrated)
        {
            safetyVolume = EncloseAutomaticPath(keyframes);
        }
        if (safetyVolume is null)
        {
            warnings.Add("CAMERA_PATH_HAS_NO_SINGLE_SAFE_VOLUME");
            return EstablishingFallback(
                candidate,
                context.Profile,
                context,
                warnings);
        }
        CameraShotPlan shot = new()
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
            Warnings = warnings,
            Family = family,
            SubjectIds = candidate.SubjectIds.Count > 0
                ? candidate.SubjectIds
                : [candidate.Trajectory.Samples.Count > 0
                    ? "selected-player"
                    : "unknown-subject"],
            TargetPoints = keyframes.Select((value, index) =>
            {
                double progress = SmoothStep(
                    index / (double)(keyframes.Count - 1));
                return new CameraTargetPoint(
                    value.TimeSeconds,
                    Lerp(startSubject, endSubject, progress),
                    candidate.SubjectIds);
            }).ToArray(),
            FovCurve = keyframes.Select(value =>
                new CameraFovPoint(value.TimeSeconds, value.Fov)).ToArray(),
            FramingIntent = context.DestinationSubjectPosition is not null
                ? "slow single A-to-B reveal ending on the next highlight location"
                : FramingIntent(family),
            MovementDirection = routeDirection,
            SafetyVolume = safetyVolume,
            PreviewRequired = true,
            AutomaticCalibration = context.Profile.AutomaticallyCalibrated,
            FallbackChain =
            [
                CameraShotFamily.StaticTripod,
                CameraShotFamily.PlayerPov
            ]
        };
        return CameraShotSignatureBuilder.Attach(shot, context.MapName);
    }

    private static CameraShotPlan CreateStaticTripod(
        BrollCandidate candidate,
        MapCameraProfile profile,
        CameraPlanningContext context,
        IReadOnlyList<string> warnings)
    {
        EstablishingCameraPreset? preset = profile.EstablishingShots
            .FirstOrDefault(value => value.Keyframes.Count > 0);
        if (preset is null)
            return Pov(candidate, [.. warnings, "VERIFIED_TRIPOD_UNAVAILABLE"]);
        CameraKeyframe source = preset.Keyframes[0];
        PlayerTransformSample[] samples = candidate.Trajectory.Samples
            .OrderBy(value => value.Tick)
            .ToArray();
        GameplayVector3 subject = SubjectPosition(
            candidate,
            0.5,
            samples[samples.Length / 2].Position);
        if (source.Position.DistanceTo(subject) >
            Math.Max(640, context.CameraDistance * 8))
        {
            return Pov(
                candidate,
                [.. warnings, "CAMERA_TARGET_OUTSIDE_VERIFIED_VOLUME"]);
        }
        CameraKeyframe keyframe = source with
        {
            TimeSeconds = 0,
            Rotation = LookAt(
                source.Position,
                Add(subject, new GameplayVector3(0, 0, 54)))
        };
        CameraShotPlan shot = new()
        {
            Id = $"camera-{candidate.Id}-{preset.Id}-tripod",
            Type = CameraShotType.StaticTripod,
            Family = CameraShotFamily.StaticTripod,
            DemoId = candidate.DemoId,
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = candidate.DurationSeconds,
            Keyframes = [keyframe],
            TargetPoints =
            [
                new CameraTargetPoint(0, subject, candidate.SubjectIds)
            ],
            FovCurve = [new CameraFovPoint(0, keyframe.Fov)],
            FovStart = keyframe.Fov,
            FovEnd = keyframe.Fov,
            FramingIntent = "subject enters and crosses a verified frame",
            SafetyVolume = profile.SafeVolumes.FirstOrDefault(value =>
                value.Contains(keyframe.Position)),
            PreviewRequired = true,
            AutomaticCalibration = profile.AutomaticallyCalibrated,
            VerifiedPresetId = preset.Id,
            RequiresHighFpsCapture = false,
            FallbackShotId = $"camera-{candidate.Id}-pov",
            FallbackChain = [CameraShotFamily.PlayerPov],
            SubjectIds = candidate.SubjectIds,
            Warnings = warnings
        };
        return CameraShotSignatureBuilder.Attach(shot, context.MapName);
    }

    private static CameraShotPlan EstablishingFallback(
        BrollCandidate candidate,
        MapCameraProfile profile,
        CameraPlanningContext context,
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
        PlayerTransformSample[] targetSamples = candidate.Trajectory.Samples
            .OrderBy(value => value.Tick)
            .ToArray();
        if (targetSamples.Length < 2)
            return Pov(
                candidate,
                [.. warnings, "CAMERA_TARGET_TRAJECTORY_INSUFFICIENT"]);
        double nearestTargetDistance = source
            .SelectMany(camera => targetSamples.Select(target =>
                camera.Position.DistanceTo(target.Position)))
            .DefaultIfEmpty(double.MaxValue)
            .Min();
        double maximumTrackingDistance = Math.Max(
            640,
            context.CameraDistance * 8);
        if (nearestTargetDistance > maximumTrackingDistance)
        {
            return Pov(
                candidate,
                [.. warnings, "CAMERA_TARGET_OUTSIDE_VERIFIED_VOLUME"]);
        }
        double sourceDuration = Math.Max(
            0.001,
            source[^1].TimeSeconds - source[0].TimeSeconds);
        CameraKeyframe[] keyframes = source
            .Select((value, index) =>
            {
                double progress = index / (double)(source.Length - 1);
                PlayerTransformSample target = Sample(
                    targetSamples,
                    progress);
                return value with
                {
                    TimeSeconds = candidate.DurationSeconds *
                        (value.TimeSeconds - source[0].TimeSeconds) /
                        sourceDuration,
                    Rotation = LookAt(
                        value.Position,
                        Add(
                            target.Position,
                            new GameplayVector3(0, 0, 54)))
                };
            })
            .ToArray();
        CameraShotFamily family = candidate.Type is
                BrollCandidateType.PlayerApproach or
                BrollCandidateType.SideMovement or
                BrollCandidateType.RearMovement or
                BrollCandidateType.TeamMovement
            ? CameraShotFamily.SideTracking
            : CameraShotFamily.EnvironmentReveal;
        CameraShotPlan shot = new()
        {
            Id = $"camera-{candidate.Id}-{preset.Id}",
            Type = candidate.Type is
                    BrollCandidateType.PlayerApproach or
                    BrollCandidateType.SideMovement or
                    BrollCandidateType.RearMovement
                ? CameraShotType.SideTracking
                : CameraShotType.EnvironmentReveal,
            DemoId = candidate.DemoId,
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = candidate.DurationSeconds,
            Keyframes = keyframes,
            FovStart = keyframes[0].Fov,
            FovEnd = keyframes[^1].Fov,
            RequiresHighFpsCapture = false,
            FallbackShotId = $"camera-{candidate.Id}-pov",
            Warnings = warnings,
            Family = family,
            SubjectIds = candidate.SubjectIds,
            TargetPoints = keyframes.Select((value, index) =>
            {
                double progress = index / (double)(keyframes.Length - 1);
                GameplayVector3 target = SubjectPosition(
                    candidate,
                    progress,
                    Sample(targetSamples, progress).Position);
                return new CameraTargetPoint(
                    value.TimeSeconds,
                    target,
                    candidate.SubjectIds);
            }).ToArray(),
            FovCurve = keyframes.Select(value =>
                new CameraFovPoint(value.TimeSeconds, value.Fov)).ToArray(),
            FramingIntent = FramingIntent(family),
            SafetyVolume = profile.SafeVolumes.FirstOrDefault(value =>
                keyframes.All(keyframe => value.Contains(keyframe.Position))),
            PreviewRequired = true,
            AutomaticCalibration = profile.AutomaticallyCalibrated,
            VerifiedPresetId = preset.Id,
            FallbackChain =
            [
                CameraShotFamily.StaticTripod,
                CameraShotFamily.PlayerPov
            ]
        };
        return CameraShotSignatureBuilder.Attach(shot, context.MapName);
    }

    public static CameraShotPlan Pov(
        BrollCandidate candidate,
        IReadOnlyList<string> warnings) =>
        CameraShotSignatureBuilder.Attach(new CameraShotPlan
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
            Warnings = warnings,
            Family = CameraShotFamily.PlayerPov,
            SubjectIds = candidate.SubjectIds,
            TargetPoints = [],
            FovCurve =
            [
                new CameraFovPoint(0, 90),
                new CameraFovPoint(candidate.DurationSeconds, 90)
            ],
            FramingIntent = "selected player POV continuity",
            PreviewRequired = false,
            FallbackChain = []
        }, string.Empty);

    private static CameraShotFamily FamilyFor(BrollCandidate candidate) =>
        candidate.Type switch
        {
            BrollCandidateType.EstablishingShot or
            BrollCandidateType.EnvironmentShot =>
                CameraShotFamily.StaticTripod,
            BrollCandidateType.TeamMovement or
            BrollCandidateType.TeamSetup => CameraShotFamily.GroupWide,
            BrollCandidateType.PlayerRotation => CameraShotFamily.Orbit,
            BrollCandidateType.PlayerJump => CameraShotFamily.SideTracking,
            BrollCandidateType.SideMovement => CameraShotFamily.SideTracking,
            BrollCandidateType.RearMovement or
            BrollCandidateType.PostFightExit => CameraShotFamily.RearTracking,
            BrollCandidateType.PlayerApproach or
            BrollCandidateType.BombApproach or
            BrollCandidateType.PreFightSetup =>
                CameraShotFamily.FrontTracking,
            BrollCandidateType.UtilityPreparation or
            BrollCandidateType.UtilityThrow or
            BrollCandidateType.WeaponDraw or
            BrollCandidateType.WeaponReload or
            BrollCandidateType.WeaponSwitch or
            BrollCandidateType.ScopePreparation =>
                CameraShotFamily.WeaponDetail,
            BrollCandidateType.PovContinuity or
            BrollCandidateType.BombPlant or
            BrollCandidateType.BombDefuse => CameraShotFamily.PlayerPov,
            _ => CameraShotFamily.RearTracking
        };

    private static GameplayVector3 SubjectPosition(
        BrollCandidate candidate,
        double progress,
        GameplayVector3 fallback)
    {
        GameplayVector3[] positions = candidate.SubjectTrajectories.Values
            .Select(value => value.Samples
                .OrderBy(sample => sample.Tick)
                .ToArray())
            .Where(value => value.Length > 0)
            .Select(value => Sample(value, progress).Position)
            .ToArray();
        if (positions.Length == 0)
            return fallback;
        return new GameplayVector3(
            positions.Average(value => value.X),
            positions.Average(value => value.Y),
            positions.Average(value => value.Z));
    }

    private static GameplayVector3 CameraEndpoint(
        GameplayVector3 subject,
        GameplayVector3 direction,
        GameplayVector3 side,
        CameraShotFamily family,
        CameraPlanningContext context,
        bool destination)
    {
        GameplayVector3 planarOffset = family switch
        {
            CameraShotFamily.SideTracking or CameraShotFamily.GroupWide =>
                Add(
                    Scale(side, context.CameraDistance *
                        (family == CameraShotFamily.GroupWide ? 1.25 : 1)),
                    Scale(direction, destination ? -36 : 20)),
            CameraShotFamily.FrontTracking =>
                Scale(direction, destination ? -72 : 72),
            CameraShotFamily.WeaponDetail => Add(
                Scale(direction, -48),
                Scale(side, 20)),
            _ => Add(
                Scale(direction, -context.CameraDistance * 0.75),
                Scale(side, destination ? -28 : 28))
        };
        return Add(
            subject,
            new GameplayVector3(
                planarOffset.X,
                planarOffset.Y,
                family == CameraShotFamily.WeaponDetail
                    ? 46
                    : context.CameraHeight));
    }

    private static GameplayVector3 Lerp(
        GameplayVector3 start,
        GameplayVector3 end,
        double progress)
    {
        double value = Math.Clamp(progress, 0, 1);
        return new GameplayVector3(
            start.X + (end.X - start.X) * value,
            start.Y + (end.Y - start.Y) * value,
            start.Z + (end.Z - start.Z) * value);
    }

    private static double SmoothStep(double value)
    {
        double progress = Math.Clamp(value, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    private static SafeCameraVolume EncloseAutomaticPath(
        IReadOnlyList<CameraKeyframe> keyframes)
    {
        const double padding = 8;
        return new SafeCameraVolume(
            new GameplayVector3(
                keyframes.Min(value => value.Position.X) - padding,
                keyframes.Min(value => value.Position.Y) - padding,
                keyframes.Min(value => value.Position.Z) - padding),
            new GameplayVector3(
                keyframes.Max(value => value.Position.X) + padding,
                keyframes.Max(value => value.Position.Y) + padding,
                keyframes.Max(value => value.Position.Z) + padding));
    }

    private static string FramingIntent(CameraShotFamily family) =>
        family switch
        {
            CameraShotFamily.StaticTripod =>
                "subject crosses a verified static composition",
            CameraShotFamily.SideTracking =>
                "medium side profile with forward lead room",
            CameraShotFamily.RearTracking =>
                "rear pursuit preserving route context",
            CameraShotFamily.FrontTracking =>
                "front approach with visible destination context",
            CameraShotFamily.GroupWide =>
                "wide composition retaining all stable team subjects",
            CameraShotFamily.Orbit =>
                "short motivated orbit around the primary subject",
            CameraShotFamily.WeaponDetail =>
                "brief weapon and hands detail without clipping",
            CameraShotFamily.EnvironmentReveal =>
                "environment reveal with subject-scale continuity",
            _ => "selected player POV continuity"
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

    private static GameplayVector3 LookAt(
        GameplayVector3 camera,
        GameplayVector3 target)
    {
        double x = target.X - camera.X;
        double y = target.Y - camera.Y;
        double z = target.Z - camera.Z;
        double horizontal = Math.Sqrt(x * x + y * y);
        return new GameplayVector3(
            -Math.Atan2(z, Math.Max(0.000001, horizontal)) * 180 / Math.PI,
            Math.Atan2(y, x) * 180 / Math.PI,
            0);
    }
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
            shot.Type is not CameraShotType.StaticEstablishing and
                not CameraShotType.StaticTripod)
            warnings.Add("CAMERA_PREVIEW_TOO_STATIC");
        if (shot.Type is not CameraShotType.PlayerPov &&
            shot.Type is not CameraShotType.StaticEstablishing &&
            shot.Type is not CameraShotType.StaticTripod &&
            shot.Keyframes.Count < 4)
            warnings.Add("CAMERA_CAMPATH_KEYFRAME_COUNT_INVALID");
        if (shot.Keyframes.Zip(shot.Keyframes.Skip(1)).Any(pair =>
                pair.First.TimeSeconds >= pair.Second.TimeSeconds))
            warnings.Add("CAMERA_KEYFRAME_TIME_ORDER_INVALID");
        if (shot.Keyframes.Any(value => value.Fov is < 20 or > 140))
            warnings.Add("CAMERA_FOV_INVALID");
        if (metrics.CameraInsideGeometry || metrics.WallIntersectionCount > 0)
            warnings.Add("CAMERA_PREVIEW_GEOMETRY_INTERSECTION");
        if (metrics.CameraTeleportCount > 0)
            warnings.Add("CAMERA_PREVIEW_TELEPORT");
        if (metrics.ModelClippingRatio is > 0.02)
            warnings.Add("CAMERA_PREVIEW_MODEL_CLIPPING");
        if (metrics.MaximumAngularVelocity is > 240)
            warnings.Add("CAMERA_PREVIEW_ANGULAR_VELOCITY_EXCESSIVE");
        if (metrics.MaximumFovVelocity is > 55)
            warnings.Add("CAMERA_PREVIEW_FOV_CHANGE_EXCESSIVE");
        if (metrics.ExcessiveMotionRatio is > 0.15)
            warnings.Add("CAMERA_PREVIEW_EXCESSIVE_MOTION");
        if (metrics.DemoPlaybackStripDetected)
            warnings.Add("CAMERA_PREVIEW_DEMO_PLAYBACK_STRIP_VISIBLE");
        if (metrics.UnexpectedHandsOnlyPresentation &&
            shot.Family != CameraShotFamily.WeaponDetail)
            warnings.Add("CAMERA_PREVIEW_UNEXPECTED_HANDS_ONLY");
        if (shot.Family != CameraShotFamily.PlayerPov)
        {
            if (metrics.SubjectVisibleRatio is null ||
                metrics.SubjectCenterDistance is null ||
                metrics.SubjectLossDurationSeconds is null)
                warnings.Add("CAMERA_PREVIEW_SUBJECT_ANALYSIS_UNAVAILABLE");
            if (metrics.SubjectVisibleRatio is < 0.92)
                warnings.Add("CAMERA_PREVIEW_SUBJECT_VISIBILITY_LOW");
            if (metrics.SubjectLossDurationSeconds is > 0.10)
                warnings.Add("CAMERA_PREVIEW_SUBJECT_LOST");
            if (metrics.SubjectClippingRatio is > 0.02)
                warnings.Add("CAMERA_PREVIEW_SUBJECT_CLIPPED");
            if (shot.Family == CameraShotFamily.GroupWide &&
                shot.SubjectIds.Count >= 2 &&
                metrics.GroupCoverageRatio is < 0.90)
                warnings.Add("CAMERA_PREVIEW_GROUP_COVERAGE_LOW");
        }
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
        TimeWarpPlan safePlan = plan with { Segments = safeSegments };
        return TryCreateMotivatedSlowMotion(
            highlight,
            safePlan,
            killOffset,
            options) ?? safePlan;
    }

    private static TimeWarpPlan? TryCreateMotivatedSlowMotion(
        SelectedHighlight highlight,
        TimeWarpPlan alignmentPlan,
        double killOffset,
        CinematicTimeWarpOptions options)
    {
        bool dynamicRange = options.MinimumLocalSpeed <= 0.75 &&
            options.MaximumLocalSpeed >= 1.20;
        int treatment = Math.Abs(highlight.SelectionOrder) % 4;
        bool multikill = highlight.Highlight.KillCount > 1;
        bool heroMoment = options.MusicEnergyTransition ||
            multikill ||
            treatment == 1 ||
            treatment == 3 &&
            highlight.Highlight.BeautyScore >= 60;
        double duration = Math.Max(
            0,
            highlight.Bounds.SafeEndSeconds -
            highlight.Bounds.SafeStartSeconds);
        if (!dynamicRange ||
            !heroMoment ||
            duration < 1.25 ||
            killOffset < 0.65 ||
            duration - killOffset < 0.20 ||
            alignmentPlan.Segments.Any(value =>
                Math.Abs(value.Speed - 1) > 0.035))
        {
            return null;
        }

        double slowDuration = multikill
            ? Math.Clamp(killOffset * 0.28, 0.30, 0.46)
            : treatment == 1
                ? Math.Clamp(killOffset * 0.18, 0.20, 0.30)
                : Math.Clamp(killOffset * 0.23, 0.25, 0.38);
        double slowSpeed = multikill
            ? Math.Clamp(options.MinimumLocalSpeed, 0.58, 0.68)
            : treatment == 1
                ? 0.78
                : 0.69;
        double delay = slowDuration / slowSpeed - slowDuration;
        double availableAcceleration = killOffset - slowDuration;
        double minimumAcceleration = delay *
            options.MaximumLocalSpeed /
            Math.Max(0.001, options.MaximumLocalSpeed - 1);
        double accelerationDuration = Math.Clamp(
            Math.Max(0.48, minimumAcceleration + 0.04),
            0.20,
            availableAcceleration);
        double denominator = accelerationDuration - delay;
        if (denominator <= 0.05)
            return null;
        double accelerationSpeed =
            accelerationDuration / denominator;
        if (accelerationSpeed > options.MaximumLocalSpeed + 0.001)
            return null;

        double accelerationStart =
            killOffset - slowDuration - accelerationDuration;
        List<TimeWarpSegment> segments = [];
        AddStylizedSegment(segments, 0, accelerationStart, 1);
        AddStylizedSegment(
            segments,
            accelerationStart,
            killOffset - slowDuration,
            accelerationSpeed);
        AddStylizedSegment(
            segments,
            killOffset - slowDuration,
            killOffset,
            slowSpeed);

        double postHold = Math.Min(0.20, duration - killOffset);
        double recoveryDuration = Math.Min(
            0.36,
            Math.Max(0, duration - killOffset - postHold));
        double postSlowSpeed = Math.Min(0.82, slowSpeed + 0.12);
        double postDelay = postHold / postSlowSpeed - postHold;
        const int recoverySteps = 3;
        double recoveryStep = recoveryDuration / recoverySteps;
        for (int index = 0; index < recoverySteps; index++)
        {
            double progress = (index + 1d) / recoverySteps;
            double speed = slowSpeed + (1 - slowSpeed) * progress;
            postDelay += recoveryStep / speed - recoveryStep;
        }
        double recoveryEnd =
            killOffset + postHold + recoveryDuration;
        double maximumRecoverySpeed = Math.Max(
            1,
            options.MaximumPostKillAcceleration);
        double compensationDuration = maximumRecoverySpeed > 1.0001
            ? postDelay * maximumRecoverySpeed /
                (maximumRecoverySpeed - 1)
            : double.MaxValue;
        bool canRecover = compensationDuration <=
            duration - recoveryEnd + 0.000001;
        if (canRecover)
        {
            AddStylizedSegment(
                segments,
                killOffset,
                killOffset + postHold,
                postSlowSpeed);
            for (int index = 0; index < recoverySteps; index++)
            {
                double start =
                    killOffset + postHold + recoveryStep * index;
                double end = start + recoveryStep;
                double progress = (index + 1d) / recoverySteps;
                AddStylizedSegment(
                    segments,
                    start,
                    end,
                    slowSpeed + (1 - slowSpeed) * progress);
            }
            AddStylizedSegment(
                segments,
                recoveryEnd,
                recoveryEnd + compensationDuration,
                maximumRecoverySpeed);
            AddStylizedSegment(
                segments,
                recoveryEnd + compensationDuration,
                duration,
                1);
        }
        else
        {
            AddStylizedSegment(
                segments,
                killOffset,
                duration,
                1);
        }
        List<string> warnings =
        [
            .. alignmentPlan.Warnings,
            "CINEMATIC_MOTIVATED_SLOW_MOTION"
        ];
        if (options.MusicEnergyTransition)
            warnings.Add("MUSIC_ENERGY_CHANGE_FIRE_SLOW_MOTION");
        return new TimeWarpPlan(
            1,
            segments,
            true,
            warnings);
    }

    private static void AddStylizedSegment(
        List<TimeWarpSegment> segments,
        double start,
        double end,
        double speed)
    {
        if (end - start <= 0.000001)
            return;
        segments.Add(new TimeWarpSegment(
            start,
            end,
            Math.Max(0.01, speed)));
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
        bool finalHighlight,
        int sequenceIndex = 0);
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
        bool finalHighlight,
        int sequenceIndex = 0)
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
        double center = Math.Clamp(
            match?.PlannedKillSeconds ?? segmentDuration / 2,
            0,
            segmentDuration);
        bool climax = finalHighlight ||
            role == CinematicSequenceRole.PeakHighlight;
        double impactStrength = Math.Clamp(
            0.42 +
            0.16 * (match?.Peak.Strength ?? section.Energy) +
            (climax ? 0.12 : 0),
            0.42,
            0.76);
        (double Start, double End) impactWindow = Window(
            center, 0.08, 0.13, segmentDuration);
        (double Start, double End) freezeWindow = Window(
            center, 0.025, 0.075, segmentDuration);
        (double Start, double End) motionWindow = Window(
            center, 0.025, 0.12, segmentDuration);
        (double Start, double End) distortionWindow = Window(
            center, 0.05, 0.07, segmentDuration);

        // The musical/gameplay event chooses the treatment. The sequence index
        // intentionally does not rotate through a catalogue of visual effects.
        // This keeps ordinary kills clean and makes rare treatments explainable.
        List<MotivatedEffectDirective> planned = [];
        if (finalHighlight)
        {
            planned.Add(Directive("HitStop", reason, freezeWindow, 0.72));
        }
        else if (match?.Peak.Type == MusicalPeakType.BassImpact &&
                 match.Peak.Strength >= 0.85 &&
                 section.Energy >= 0.75)
        {
            planned.Add(Directive(
                "LensWarpPulse",
                MotivatedEffectReason.BassImpact,
                distortionWindow,
                0.44));
        }
        else if (match?.Peak.Type == MusicalPeakType.DropStart &&
                 match.Peak.Strength >= 0.72)
        {
            planned.Add(Directive(
                "PunchZoom",
                MotivatedEffectReason.MusicPeak,
                impactWindow,
                impactStrength));
        }
        else if (role == CinematicSequenceRole.PeakHighlight &&
                 section.Energy >= 0.80 &&
                 (match?.Peak.Strength ?? 0) >= 0.70)
        {
            planned.Add(Directive(
                "RecoilShake",
                reason,
                motionWindow,
                0.46));
        }
        return planned
            .Where(value =>
                value.EndSeconds - value.StartSeconds >= 0.04)
            .Take(policy.MaximumVisibleFilterEffectsPerHighlight)
            .ToArray();
    }

    private static MotivatedEffectDirective Directive(
        string effectType,
        MotivatedEffectReason reason,
        (double Start, double End) window,
        double intensity) =>
        new(
            effectType,
            reason,
            window.Start,
            window.End,
            intensity);

    private static (double Start, double End) Window(
        double center,
        double pre,
        double post,
        double duration)
    {
        double width = pre + post;
        if (duration <= width)
            return (0, Math.Max(0, duration));
        double start = center - pre;
        double end = center + post;
        if (start < 0)
        {
            end -= start;
            start = 0;
        }
        if (end > duration)
        {
            start -= end - duration;
            end = duration;
        }
        return (Math.Max(0, start), Math.Min(duration, end));
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
                    new SoundDesignSection(value.Id, -18, -3, true, false),
                MusicSectionType.BuildUp or MusicSectionType.PreDrop =>
                    new SoundDesignSection(value.Id, -13, -3, true, false),
                MusicSectionType.Drop or MusicSectionType.Chorus or
                    MusicSectionType.HighEnergy =>
                    new SoundDesignSection(value.Id, -6, -3, false, false),
                _ => new SoundDesignSection(value.Id, -15, -3, false, false)
            }).ToArray(),
            PreservePostKillTail: true,
            Warnings: ["MUSIC_GAIN_STABLE_AROUND_KILLS"]);
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
                    new ColorNarrativeSection(value.Id, 0.94, -0.035),
                MusicSectionType.BuildUp or MusicSectionType.PreDrop =>
                    new ColorNarrativeSection(value.Id, 1.01, 0),
                MusicSectionType.Drop or MusicSectionType.Chorus or
                    MusicSectionType.HighEnergy =>
                    new ColorNarrativeSection(value.Id, 1.06, 0.012),
                MusicSectionType.Outro =>
                    new ColorNarrativeSection(value.Id, 0.93, -0.065),
                _ => new ColorNarrativeSection(value.Id, 0.98, -0.012)
            }).ToArray(),
            []);
}
