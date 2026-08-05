using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class AutomaticCameraCalibrationStoreTests
{
    [Fact]
    public async Task AcceptedAutomaticShotIsPersistedByMapAndHlaeVersion()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "cs2-highlight-camera-calibration-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            CinematicCameraRuntimeOptions options = new()
            {
                CalibrationRoot = root
            };
            using AutomaticCameraCalibrationStore store = new(
                options,
                TimeProvider.System);
            SafeCameraVolume volume = new(
                new GameplayVector3(-128, -128, 0),
                new GameplayVector3(128, 128, 192));
            CameraShotPlan shot = CameraShotSignatureBuilder.Attach(
                new CameraShotPlan
                {
                    Id = "auto-shot",
                    Type = CameraShotType.SideTracking,
                    Family = CameraShotFamily.SideTracking,
                    DemoId = "demo",
                    StartTick = 100,
                    EndTick = 292,
                    TargetDurationSeconds = 3,
                    Keyframes = Enumerable.Range(0, 4)
                        .Select(index => new CameraKeyframe
                        {
                            TimeSeconds = index,
                            Position = new GameplayVector3(
                                index * 20,
                                40,
                                80),
                            Rotation = GameplayVector3.Zero,
                            Fov = 82
                        })
                        .ToArray(),
                    FovStart = 82,
                    FovEnd = 82,
                    RequiresHighFpsCapture = false,
                    FallbackShotId = "pov",
                    Warnings = [],
                    SafetyVolume = volume,
                    AutomaticCalibration = true
                },
                "de_newmap");

            await store.MergeAcceptedAsync(
                "de_newmap",
                "2.191.1",
                [shot],
                CancellationToken.None);
            MapCameraProfile? loaded = await store.LoadAsync(
                "de_newmap",
                "2.191.1",
                CancellationToken.None);
            MapCameraProfile? wrongVersion = await store.LoadAsync(
                "de_newmap",
                "2.192.0",
                CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.True(loaded.AutomaticallyCalibrated);
            Assert.Single(loaded.SafeVolumes);
            Assert.Single(loaded.EstablishingShots);
            Assert.Null(wrongVersion);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
