using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public sealed record AutomaticCameraCalibrationReport(
    string SchemaVersion,
    string MapName,
    int InputFrameCount,
    int EligibleFrameCount,
    int CoveredTrajectoryCount,
    IReadOnlyList<SafeCameraVolume> SafeVolumes,
    IReadOnlyList<EstablishingCameraPreset> EstablishingShots);

public sealed record AutomaticCameraCalibrationResult(
    MapCameraProfile Profile,
    AutomaticCameraCalibrationReport Report);

public interface IAutomaticMapCameraCalibrator
{
    AutomaticCameraCalibrationResult Calibrate(
        string mapName,
        IReadOnlyList<GameplayTimelineFrame> frames,
        int tickRate,
        MapCameraProfile? acceptedProfile = null);
}

public sealed class AutomaticMapCameraCalibrator : IAutomaticMapCameraCalibrator
{
    private const double MaximumHorizontalSpan = 384;
    private const double MaximumVerticalSpan = 192;
    private const double HorizontalPadding = 128;
    private const double LowerPadding = 32;
    private const double UpperPadding = 112;
    private const double HorizontalCellSize = 256;
    private const double VerticalCellSize = 128;

    public AutomaticCameraCalibrationResult Calibrate(
        string mapName,
        IReadOnlyList<GameplayTimelineFrame> frames,
        int tickRate,
        MapCameraProfile? acceptedProfile = null)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            throw new ArgumentException("Map name is required.", nameof(mapName));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);

        GameplayTimelineFrame[] eligible = frames
            .Where(value =>
                value.Alive &&
                !value.InFreezeTime &&
                IsFinite(value.Player.Position))
            .OrderBy(value => value.Player.PlayerId, StringComparer.Ordinal)
            .ThenBy(value => value.RoundNumber)
            .ThenBy(value => value.Tick)
            .ToArray();
        List<GameplayTimelineFrame[]> trajectories = [];
        foreach (IGrouping<(string PlayerId, int Round), GameplayTimelineFrame> group in
                 eligible.GroupBy(value =>
                     (value.Player.PlayerId, value.RoundNumber)))
        {
            List<GameplayTimelineFrame> current = [];
            foreach (GameplayTimelineFrame frame in group)
            {
                bool gap = current.Count > 0 &&
                    frame.Tick - current[^1].Tick > tickRate;
                bool tooLarge = current.Count > 0 &&
                    ExceedsMaximumSpan(current, frame.Player.Position);
                if (gap || tooLarge)
                {
                    AddTrajectory(trajectories, current);
                    current.Clear();
                }
                current.Add(frame);
            }
            AddTrajectory(trajectories, current);
        }

        SafeCameraVolume[] volumes = eligible
            .GroupBy(value => CellKey(value.Player.Position))
            .Select(value => ToCellVolume(value.First().Player.Position))
            .Concat(acceptedProfile?.SafeVolumes ?? [])
            .GroupBy(VolumeKey, StringComparer.Ordinal)
            .Select(value => value.First())
            .OrderBy(value => value.Minimum.X)
            .ThenBy(value => value.Minimum.Y)
            .ThenBy(value => value.Minimum.Z)
            .ToArray();
        EstablishingCameraPreset[] presets = volumes
            .Select((volume, index) => ToPreset(mapName, volume, index))
            .Concat(acceptedProfile?.EstablishingShots ?? [])
            .GroupBy(value => value.Id, StringComparer.Ordinal)
            .Select(value => value.First())
            .ToArray();
        MapCameraProfile profile = new()
        {
            MapName = mapName,
            SafeVolumes = volumes,
            EstablishingShots = presets,
            RestrictedVolumes = [],
            ManuallyVerified = false,
            AutomaticallyCalibrated = volumes.Length > 0
        };
        return new AutomaticCameraCalibrationResult(
            profile,
            new AutomaticCameraCalibrationReport(
                "1.0",
                mapName,
                frames.Count,
                eligible.Length,
                trajectories.Count,
                volumes,
                presets));
    }

    private static void AddTrajectory(
        List<GameplayTimelineFrame[]> result,
        List<GameplayTimelineFrame> frames)
    {
        if (frames.Count < 2)
            return;
        if (frames[0].Player.Position.DistanceTo(
                frames[^1].Player.Position) < 24)
            return;
        result.Add(frames.ToArray());
    }

    private static bool ExceedsMaximumSpan(
        IReadOnlyList<GameplayTimelineFrame> frames,
        GameplayVector3 candidate)
    {
        double minX = Math.Min(candidate.X, frames.Min(value =>
            value.Player.Position.X));
        double maxX = Math.Max(candidate.X, frames.Max(value =>
            value.Player.Position.X));
        double minY = Math.Min(candidate.Y, frames.Min(value =>
            value.Player.Position.Y));
        double maxY = Math.Max(candidate.Y, frames.Max(value =>
            value.Player.Position.Y));
        double minZ = Math.Min(candidate.Z, frames.Min(value =>
            value.Player.Position.Z));
        double maxZ = Math.Max(candidate.Z, frames.Max(value =>
            value.Player.Position.Z));
        return maxX - minX > MaximumHorizontalSpan ||
            maxY - minY > MaximumHorizontalSpan ||
            maxZ - minZ > MaximumVerticalSpan;
    }

    private static SafeCameraVolume ToCellVolume(GameplayVector3 point)
    {
        double x = Math.Floor(point.X / HorizontalCellSize) *
            HorizontalCellSize;
        double y = Math.Floor(point.Y / HorizontalCellSize) *
            HorizontalCellSize;
        double z = Math.Floor(point.Z / VerticalCellSize) *
            VerticalCellSize;
        return new SafeCameraVolume(
            new GameplayVector3(
                x - HorizontalPadding,
                y - HorizontalPadding,
                z - LowerPadding),
            new GameplayVector3(
                x + HorizontalCellSize + HorizontalPadding,
                y + HorizontalCellSize + HorizontalPadding,
                z + VerticalCellSize + UpperPadding));
    }

    private static EstablishingCameraPreset ToPreset(
        string mapName,
        SafeCameraVolume volume,
        int index)
    {
        GameplayVector3 subject = new(
            (volume.Minimum.X + volume.Maximum.X) / 2,
            (volume.Minimum.Y + volume.Maximum.Y) / 2,
            volume.Minimum.Z + LowerPadding);
        GameplayVector3 camera = new(
            Math.Min(volume.Maximum.X, subject.X + 96),
            Math.Min(volume.Maximum.Y, subject.Y + 96),
            Math.Min(volume.Maximum.Z, subject.Z + 52));
        return new EstablishingCameraPreset(
            $"auto-{mapName}-{index:D3}",
            [
                new CameraKeyframe
                {
                    TimeSeconds = 0,
                    Position = camera,
                    Rotation = LookAt(
                        camera,
                        new GameplayVector3(
                            subject.X,
                            subject.Y,
                            subject.Z + 54)),
                    Fov = 88
                }
            ]);
    }

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

    private static bool IsFinite(GameplayVector3 value) =>
        double.IsFinite(value.X) &&
        double.IsFinite(value.Y) &&
        double.IsFinite(value.Z);

    private static string VolumeKey(SafeCameraVolume volume) =>
        $"{Math.Round(volume.Minimum.X / 32)}:" +
        $"{Math.Round(volume.Minimum.Y / 32)}:" +
        $"{Math.Round(volume.Minimum.Z / 32)}:" +
        $"{Math.Round(volume.Maximum.X / 32)}:" +
        $"{Math.Round(volume.Maximum.Y / 32)}:" +
        $"{Math.Round(volume.Maximum.Z / 32)}";

    private static string CellKey(GameplayVector3 point) =>
        $"{Math.Floor(point.X / HorizontalCellSize)}:" +
        $"{Math.Floor(point.Y / HorizontalCellSize)}:" +
        $"{Math.Floor(point.Z / VerticalCellSize)}";
}
