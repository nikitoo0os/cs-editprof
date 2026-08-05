using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class RenderJobValidatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"render-agent-tests-{Guid.NewGuid():N}");
    private readonly string demo;

    public RenderJobValidatorTests()
    {
        Directory.CreateDirectory(root);
        demo = Path.Combine(root, "match.dem");
        File.WriteAllBytes(demo, [1, 2, 3]);
    }

    [Fact]
    public void ValidJobPasses()
    {
        ValidationReport result = RenderJobValidator.Validate(ValidJob(), new RenderEnvironmentOptions());
        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void InvalidTickRangeIsReported()
    {
        RenderJob job = ValidJob() with { Segment = new RenderSegment(10, 10) };
        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());
        Assert.Contains(result.Errors, error => error.Contains("endTick", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeJobIdIsReported()
    {
        RenderJob job = ValidJob() with { JobId = "../escape" };
        Assert.False(RenderJobValidator.Validate(job, new RenderEnvironmentOptions()).IsValid);
    }

    [Fact]
    public void MissingPlayerSteamIdIsReported()
    {
        RenderJob job = ValidJob() with
        {
            Player = new PlayerSelector(null, "Player")
        };

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(result.Errors, error => error.Contains("player.steamId", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidPlayerSteamIdIsReported()
    {
        RenderJob job = ValidJob() with
        {
            Player = new PlayerSelector("123", "Player")
        };

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(result.Errors, error => error.Contains("valid individual SteamID64", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPlayerNameIsAllowed()
    {
        RenderJob job = ValidJob() with
        {
            Player = new PlayerSelector("76561198000000001", null)
        };

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void BatchControlFilesAreAllowedInOutputDirectory()
    {
        RenderJob job = ValidJob();
        Directory.CreateDirectory(Path.Combine(job.OutputDirectory, "logs"));
        File.WriteAllText(Path.Combine(job.OutputDirectory, "render-job.json"), "{}");

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void ExistingRenderArtifactStillPreventsOverwrite()
    {
        RenderJob job = ValidJob();
        Directory.CreateDirectory(job.OutputDirectory);
        File.WriteAllBytes(Path.Combine(job.OutputDirectory, "raw-highlight.mp4"), [1]);

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(result.Errors, error => error.Contains("not empty", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstPersonWeaponFireWithHiddenWeaponIsRejected()
    {
        RenderJob job = ValidJob() with
        {
            PresentationMode = CapturePresentationMode.CinematicBroll,
            ContainsFirstPersonWeaponFire = true
        };

        ValidationReport result =
            RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(
            result.Errors,
            error => error.StartsWith(
                "WEAPON_HIDDEN_DURING_POV_COMBAT",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PovCombatWithWeaponFireIsAccepted()
    {
        RenderJob job = ValidJob() with
        {
            PresentationMode = CapturePresentationMode.PovCombat,
            ContainsFirstPersonWeaponFire = true
        };

        ValidationReport result =
            RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void PlayerPovWithHiddenHudIsRejectedEvenWithoutWeaponFire()
    {
        RenderJob job = ValidJob() with
        {
            PresentationMode = CapturePresentationMode.CinematicBroll,
            ContainsFirstPersonWeaponFire = false,
            Camera = RenderCameraPlan.PlayerPov
        };

        ValidationReport result =
            RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(
            result.Errors,
            error => error.StartsWith(
                "POV_CAMERA_REQUIRES_HUD",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CalibrationStaticCameraInsideSafeVolumeIsAccepted()
    {
        RenderJob job = ValidJob() with
        {
            PresentationMode = CapturePresentationMode.EstablishingShot,
            ContainsFirstPersonWeaponFire = false,
            Camera = StaticCamera(calibrationSpike: true)
        };

        ValidationReport result =
            RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void UnverifiedProductionCameraIsRejected()
    {
        RenderJob job = ValidJob() with
        {
            PresentationMode = CapturePresentationMode.CinematicBroll,
            ContainsFirstPersonWeaponFire = false,
            Camera = StaticCamera(calibrationSpike: false)
        };

        ValidationReport result =
            RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "manual-spike verification",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CameraOutsideSafeVolumeIsRejected()
    {
        RenderCameraPlan camera = StaticCamera(calibrationSpike: true) with
        {
            Keyframes =
            [
                new RenderCameraKeyframe(
                    15,
                    new RenderVector3(999, 2, 3),
                    new RenderVector3(0, 0, 0),
                    90)
            ]
        };
        RenderJob job = ValidJob() with
        {
            PresentationMode = CapturePresentationMode.EstablishingShot,
            ContainsFirstPersonWeaponFire = false,
            Camera = camera
        };

        ValidationReport result =
            RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(
            result.Errors,
            error => error.Contains("outside", StringComparison.Ordinal));
    }

    private static RenderCameraPlan StaticCamera(bool calibrationSpike) =>
        new()
        {
            Mode = RenderCameraMode.Static,
            MapName = "de_dust2",
            CalibrationSpike = calibrationSpike,
            VerificationId = "test",
            HlaeVersionPrefix = "2.191.1",
            SafeVolume = new RenderCameraBounds(
                new RenderVector3(0, 0, 0),
                new RenderVector3(10, 10, 10)),
            Keyframes =
            [
                new RenderCameraKeyframe(
                    15,
                    new RenderVector3(1, 2, 3),
                    new RenderVector3(0, 90, 0),
                    90)
            ]
        };

    private RenderJob ValidJob() => new(
        "job-1", demo, new PlayerSelector("76561198000000001", "Player"),
        new RenderSegment(10, 20), new VideoSettings(1920, 1080, 60, 90),
        Path.Combine(root, "output"), 60);

    public void Dispose() => Directory.Delete(root, true);
}
