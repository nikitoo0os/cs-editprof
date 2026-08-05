using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class ProfessionalCinematicPolicyTests
{
    [Fact]
    public void SignatureIncludesTheExactSourceInterval()
    {
        CameraShotPlan first = CameraShotSignatureBuilder.Attach(
            Shot("first", 100, 200, CameraShotFamily.SideTracking),
            "de_dust2");
        CameraShotPlan second = CameraShotSignatureBuilder.Attach(
            Shot("second", 201, 300, CameraShotFamily.SideTracking),
            "de_dust2");

        Assert.NotEqual(
            first.Signature!.DeterministicHash,
            second.Signature!.DeterministicHash);
        Assert.Equal("demo:100-200", first.Signature.SourceInterval);
    }

    [Fact]
    public void DiversityRejectsAnAdjacentRepeatedCameraFamily()
    {
        CameraShotPlan first = CameraShotSignatureBuilder.Attach(
            Shot("first", 100, 200, CameraShotFamily.SideTracking),
            "de_dust2");

        ShotDiversityDecision decision = ShotDiversityPolicy.Evaluate(
            Shot("second", 300, 400, CameraShotFamily.SideTracking),
            "de_dust2",
            [first]);

        Assert.False(decision.Accepted);
        Assert.Contains(
            "ADJACENT_CAMERA_FAMILY_REPEATED",
            decision.RejectionReasons);
    }

    [Fact]
    public void BulletPathIsUnavailableWithoutAnExactImpactPoint()
    {
        KillEvent kill = new(
            0,
            128,
            1,
            "killer",
            "Killer",
            "victim",
            "Victim",
            null,
            "ak47",
            true,
            "T",
            "CT")
        {
            ShooterPosition = new GameplayVector3(10, 20, 30),
            HitPosition = null,
            BulletTrajectoryStatus = "UnavailableExactImpact"
        };

        BulletPathCandidateResult result = BulletPathShotPlanner.Create(
            kill,
            "demo",
            "de_dust2",
            64,
            new SafeCameraVolume(
                new GameplayVector3(-100, -100, -100),
                new GameplayVector3(100, 100, 100)));

        Assert.False(result.Available);
        Assert.Null(result.Shot);
        Assert.Equal(
            "BULLET_PATH_EXACT_TRAJECTORY_UNAVAILABLE",
            result.UnavailableReason);
    }

    [Fact]
    public void EffectRarityAllowsAtMostTwoShortNonAdjacentLensWarps()
    {
        CinematicSequenceSegment[] source = Enumerable.Range(0, 4)
            .Select(index => SegmentWithLensWarp(index))
            .ToArray();

        IReadOnlyList<CinematicSequenceSegment> result =
            EffectRarityPolicy.Apply(source, out EffectRarityReport report);

        MotivatedEffectDirective[] accepted = result
            .SelectMany(value => value.Effects)
            .ToArray();
        Assert.Equal(2, accepted.Length);
        Assert.Equal(2, report.LensWarpCount);
        Assert.Empty(report.Violations);
        Assert.All(accepted, effect => Assert.InRange(
            effect.EndSeconds - effect.StartSeconds,
            EffectRarityPolicy.MinimumLensWarpSeconds,
            EffectRarityPolicy.MaximumLensWarpSeconds + 0.000001));
        Assert.Empty(result[1].Effects);
        Assert.Empty(result[3].Effects);
    }

    [Fact]
    public void CameraQualityFailsClosedWhenSubjectAnalysisIsMissing()
    {
        CameraShotPlan shot = Shot(
            "tracking",
            100,
            200,
            CameraShotFamily.SideTracking);
        CameraPreviewMetrics metrics = new(
            1.5,
            0.4,
            0,
            0.2,
            0.2,
            0,
            0.1,
            true);

        IReadOnlyList<string> warnings =
            new CameraShotQualityAnalyzer().Validate(shot, metrics);

        Assert.Contains(
            "CAMERA_PREVIEW_SUBJECT_ANALYSIS_UNAVAILABLE",
            warnings);
    }

    [Fact]
    public void DemoUiDetectorFindsAPersistentLowerPlaybackStrip()
    {
        const int width = 64;
        const int height = 48;
        byte[] frames = new byte[width * height * 3];
        for (int frame = 0; frame < 3; frame++)
        {
            int frameOffset = frame * width * height;
            for (int y = 0; y < height; y++)
            {
                byte value = y < 37 ? (byte)135 : (byte)18;
                Array.Fill(
                    frames,
                    value,
                    frameOffset + y * width,
                    width);
            }
        }

        DemoUiDetectionReport report = DemoUiDetector.AnalyzeGrayFrames(
            frames,
            width,
            height);

        Assert.True(report.Analyzed);
        Assert.True(report.DemoPlaybackStripDetected);
        Assert.Equal(3, report.FramesMatched);
        Assert.InRange(report.BoundaryRatio!.Value, 0.75, 0.80);
    }

    [Fact]
    public void DemoUiDetectorDoesNotInventAStripOnAFlatFrame()
    {
        byte[] frames = Enumerable.Repeat((byte)112, 64 * 48 * 2).ToArray();

        DemoUiDetectionReport report = DemoUiDetector.AnalyzeGrayFrames(
            frames,
            64,
            48);

        Assert.True(report.Analyzed);
        Assert.False(report.DemoPlaybackStripDetected);
    }

    [Fact]
    public void DemoUiDetectorDoesNotTreatWideGameplayHudAsPlaybackStrip()
    {
        const int width = 64;
        const int height = 48;
        byte[] frames = new byte[width * height * 3];
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            int frameOffset = frameIndex * width * height;
            Array.Fill(frames, (byte)135, frameOffset, width * height);

            // A dark lower HUD can cover almost half the picture and create
            // the same strong, persistent edge that caused the production
            // false positive. A real demo strip spans most of the width.
            for (int y = 37; y < height; y++)
            {
                Array.Fill(
                    frames,
                    (byte)18,
                    frameOffset + y * width,
                    29);
            }
        }

        DemoUiDetectionReport report = DemoUiDetector.AnalyzeGrayFrames(
            frames,
            width,
            height);

        Assert.True(report.Analyzed);
        Assert.False(report.DemoPlaybackStripDetected);
        Assert.Equal(0, report.FramesMatched);
    }

    [Fact]
    public void DemoUiDetectorFindsStripWhenHudHasAStrongerUnrelatedEdge()
    {
        const int width = 64;
        const int height = 48;
        byte[] frames = new byte[width * height * 3];
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            int frameOffset = frameIndex * width * height;
            for (int y = 0; y < height; y++)
            {
                byte value = y < 42 ? (byte)135 : (byte)18;
                Array.Fill(frames, value, frameOffset + y * width, width);
            }

            // A high-contrast HUD row has the strongest raw edge, but it does
            // not span enough of the frame to be a playback-strip boundary.
            for (int x = 0; x < 26; x++)
                frames[frameOffset + 35 * width + x] = 255;
        }

        DemoUiDetectionReport report = DemoUiDetector.AnalyzeGrayFrames(
            frames,
            width,
            height);

        Assert.True(report.DemoPlaybackStripDetected);
        Assert.Equal(3, report.FramesMatched);
        Assert.InRange(report.BoundaryRatio!.Value, 0.86, 0.89);
    }

    private static CinematicSequenceSegment SegmentWithLensWarp(int index) =>
        new()
        {
            Id = $"segment-{index}",
            Role = CinematicSequenceRole.PeakHighlight,
            OutputStartSeconds = index,
            OutputEndSeconds = index + 1,
            MusicSectionId = "drop",
            HighlightId = $"highlight-{index}",
            Camera = Shot(
                $"camera-{index}",
                index * 100,
                index * 100 + 50,
                CameraShotFamily.PlayerPov),
            TimeWarp = new TimeWarpPlan(1, [], false, []),
            Effects =
            [
                new MotivatedEffectDirective(
                    "LensWarpPulse",
                    MotivatedEffectReason.BassImpact,
                    0.20,
                    0.55,
                    0.8)
            ]
        };

    private static CameraShotPlan Shot(
        string id,
        long startTick,
        long endTick,
        CameraShotFamily family) => new()
    {
        Id = id,
        Type = family == CameraShotFamily.PlayerPov
            ? CameraShotType.PlayerPov
            : CameraShotType.SideTracking,
        Family = family,
        DemoId = "demo",
        StartTick = startTick,
        EndTick = endTick,
        TargetDurationSeconds = 1.5,
        Keyframes =
        [
            new CameraKeyframe
            {
                TimeSeconds = 0,
                Position = new GameplayVector3(startTick, 0, 32),
                Rotation = GameplayVector3.Zero,
                Fov = 90
            },
            new CameraKeyframe
            {
                TimeSeconds = 1.5,
                Position = new GameplayVector3(endTick, 32, 32),
                Rotation = GameplayVector3.Zero,
                Fov = 88
            }
        ],
        TargetPoints =
        [
            new CameraTargetPoint(
                0,
                new GameplayVector3(startTick + 64, 0, 32),
                ["player"]),
            new CameraTargetPoint(
                1.5,
                new GameplayVector3(endTick + 64, 32, 32),
                ["player"])
        ],
        SubjectIds = ["player"],
        FovStart = 90,
        FovEnd = 88,
        RequiresHighFpsCapture = false,
        FallbackShotId = string.Empty,
        Warnings = []
    };
}
