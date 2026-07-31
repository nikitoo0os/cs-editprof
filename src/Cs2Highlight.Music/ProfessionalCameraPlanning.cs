using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public static class SourceIntervalPolicy
{
    public static bool OverlapsAny(
        string candidate,
        IEnumerable<string> selected) =>
        selected.Any(value => Overlaps(candidate, value));

    public static bool Overlaps(string left, string right)
    {
        if (!TryParse(left, out ParsedSourceInterval first) ||
            !TryParse(right, out ParsedSourceInterval second))
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
        return string.Equals(
                   first.SourceId,
                   second.SourceId,
                   StringComparison.Ordinal) &&
               first.StartTick < second.EndTick &&
               second.StartTick < first.EndTick;
    }

    private static bool TryParse(
        string value,
        out ParsedSourceInterval interval)
    {
        int colon = value.LastIndexOf(':');
        int dash = colon < 0 ? -1 : value.IndexOf('-', colon + 1);
        if (colon <= 0 || dash <= colon + 1 || dash >= value.Length - 1 ||
            !long.TryParse(
                value.AsSpan(colon + 1, dash - colon - 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long start) ||
            !long.TryParse(
                value.AsSpan(dash + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long end))
        {
            interval = default;
            return false;
        }
        interval = new ParsedSourceInterval(
            value[..colon],
            Math.Min(start, end),
            Math.Max(start, end));
        return interval.EndTick > interval.StartTick;
    }

    private readonly record struct ParsedSourceInterval(
        string SourceId,
        long StartTick,
        long EndTick);
}

public sealed record ShotDiversityDecision(
    bool Accepted,
    CameraShotPlan Shot,
    IReadOnlyList<string> RejectionReasons);

public sealed record ShotDiversityReport(
    string SchemaVersion,
    int ShotCount,
    int UniqueSignatureCount,
    int UniqueSourceIntervalCount,
    int NonPovFamilyCount,
    int AccentShotCount,
    IReadOnlyList<string> Violations);

public static class ShotDiversityPolicy
{
    public const string Version = "2.0";

    public static ShotDiversityDecision Evaluate(
        CameraShotPlan candidate,
        string mapName,
        IReadOnlyList<CameraShotPlan> selected)
    {
        CameraShotPlan shot = CameraShotSignatureBuilder.Attach(
            candidate,
            mapName);
        CameraShotSignature signature = shot.Signature!;
        List<string> reasons = [];
        CameraShotPlan? previous = selected.Count > 0
            ? selected[^1]
            : null;
        if (selected.Any(value =>
                value.Signature?.DeterministicHash ==
                signature.DeterministicHash))
            reasons.Add("CAMERA_SIGNATURE_REUSED");
        if (selected.Any(value =>
                value.Signature is not null &&
                SourceIntervalPolicy.Overlaps(
                    value.Signature.SourceInterval,
                    signature.SourceInterval)))
            reasons.Add("SOURCE_INTERVAL_REUSED");
        if (previous is not null &&
            previous.Family == shot.Family &&
            shot.Family != CameraShotFamily.PlayerPov)
            reasons.Add("ADJACENT_CAMERA_FAMILY_REPEATED");
        if (shot.VerifiedPresetId is not null &&
            selected.Any(value => string.Equals(
                value.VerifiedPresetId,
                shot.VerifiedPresetId,
                StringComparison.Ordinal)))
            reasons.Add("VERIFIED_CAMERA_PRESET_REUSED");
        if (shot.Family != CameraShotFamily.PlayerPov &&
            selected.Any(value =>
                value.Family != CameraShotFamily.PlayerPov &&
                string.Equals(
                    value.Signature?.ApproximateStartCell,
                    signature.ApproximateStartCell,
                    StringComparison.Ordinal)))
            reasons.Add("CAMERA_START_POSITION_REUSED");
        if (signature.MovementVector != "0:0:0" &&
            selected.Count(value => string.Equals(
                value.Signature?.MovementVector,
                signature.MovementVector,
                StringComparison.Ordinal)) >= 2)
            reasons.Add("CAMERA_MOVEMENT_DIRECTION_OVERUSED");
        return new ShotDiversityDecision(
            reasons.Count == 0,
            shot,
            reasons);
    }

    public static ShotDiversityReport AnalyzeFilm(
        IReadOnlyList<CameraShotPlan> shots,
        double durationSeconds)
    {
        CameraShotPlan[] signed = shots
            .Select(value => value.Signature is null
                ? CameraShotSignatureBuilder.Attach(value, string.Empty)
                : value)
            .ToArray();
        List<string> violations = [];
        int signatureCount = signed
            .Select(value => value.Signature!.DeterministicHash)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int intervalCount = signed
            .Select(value => value.Signature!.SourceInterval)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int nonPovFamilies = signed
            .Where(value => value.Family != CameraShotFamily.PlayerPov)
            .Select(value => value.Family)
            .Distinct()
            .Count();
        CameraShotFamily[] accentFamilies =
        [
            CameraShotFamily.Orbit,
            CameraShotFamily.WeaponDetail,
            CameraShotFamily.BulletPath
        ];
        int accents = signed.Count(value => accentFamilies.Contains(
            value.Family));
        if (signatureCount != signed.Length)
            violations.Add("CAMERA_SIGNATURE_REUSED");
        if (intervalCount != signed.Length)
            violations.Add("SOURCE_INTERVAL_REUSED");
        if (signed.Select((shot, index) => (shot, index)).Any(value =>
                signed.Take(value.index).Any(previous =>
                    SourceIntervalPolicy.Overlaps(
                        previous.Signature!.SourceInterval,
                        value.shot.Signature!.SourceInterval))))
            violations.Add("SOURCE_INTERVAL_OVERLAP");
        if (signed.Zip(signed.Skip(1)).Any(pair =>
                pair.First.Family == pair.Second.Family &&
                pair.First.Family != CameraShotFamily.PlayerPov))
            violations.Add("ADJACENT_CAMERA_FAMILY_REPEATED");
        if (durationSeconds is >= 20 and <= 30 &&
            signed.Any(value => value.Family != CameraShotFamily.PlayerPov) &&
            nonPovFamilies < 3)
            violations.Add("CAMERA_FAMILY_DIVERSITY_BELOW_TARGET");
        if (signed.Length > 0 && accents > Math.Max(2, signed.Length / 3))
            violations.Add("ACCENT_CAMERA_SHOTS_DOMINATE");
        return new ShotDiversityReport(
            "1.0",
            signed.Length,
            signatureCount,
            intervalCount,
            nonPovFamilies,
            accents,
            violations);
    }
}

public static class CameraShotSignatureBuilder
{
    public static CameraShotPlan Attach(
        CameraShotPlan shot,
        string mapName)
    {
        GameplayVector3 start = shot.Keyframes.Count > 0
            ? shot.Keyframes[0].Position
            : GameplayVector3.Zero;
        GameplayVector3 end = shot.Keyframes.Count > 0
            ? shot.Keyframes[^1].Position
            : start;
        GameplayVector3 movement = new(
            end.X - start.X,
            end.Y - start.Y,
            end.Z - start.Z);
        double length = Math.Sqrt(
            movement.X * movement.X +
            movement.Y * movement.Y +
            movement.Z * movement.Z);
        string vector = length < 0.01
            ? "0:0:0"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Math.Round(movement.X / length, 1):0.0}:" +
                $"{Math.Round(movement.Y / length, 1):0.0}:" +
                $"{Math.Round(movement.Z / length, 1):0.0}");
        string startCell = Cell(start);
        string endCell = Cell(end);
        string interval = $"{shot.DemoId}:{shot.StartTick}-{shot.EndTick}";
        string subjects = string.Join(',', shot.SubjectIds
            .OrderBy(value => value, StringComparer.Ordinal));
        string fovRange = string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Round(Math.Min(shot.FovStart, shot.FovEnd)):0}-" +
            $"{Math.Round(Math.Max(shot.FovStart, shot.FovEnd)):0}");
        double orbitCross = 0;
        if (shot.Family == CameraShotFamily.Orbit &&
            shot.TargetPoints.Count > 0 &&
            shot.Keyframes.Count > 1)
        {
            GameplayVector3 targetStart = shot.TargetPoints[0].Position;
            GameplayVector3 targetEnd = shot.TargetPoints[^1].Position;
            GameplayVector3 radiusStart = new(
                start.X - targetStart.X,
                start.Y - targetStart.Y,
                0);
            GameplayVector3 radiusEnd = new(
                end.X - targetEnd.X,
                end.Y - targetEnd.Y,
                0);
            orbitCross = radiusStart.X * radiusEnd.Y -
                radiusStart.Y * radiusEnd.X;
        }
        string orbit = shot.Family == CameraShotFamily.Orbit
            ? Math.Sign(orbitCross) switch
            {
                < 0 => "clockwise",
                > 0 => "counter-clockwise",
                _ => "unknown"
            }
            : "none";
        string canonical = string.Join(
            '|',
            shot.Family,
            mapName,
            interval,
            subjects,
            startCell,
            endCell,
            vector,
            fovRange,
            orbit,
            shot.FramingIntent);
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return shot with
        {
            MovementDirection = movement,
            Signature = new CameraShotSignature
            {
                Family = shot.Family,
                MapName = mapName,
                SourceInterval = interval,
                SubjectIds = shot.SubjectIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                ApproximateStartCell = startCell,
                ApproximateEndCell = endCell,
                MovementVector = vector,
                FovRange = fovRange,
                OrbitDirection = orbit,
                FramingClass = shot.FramingIntent,
                DeterministicHash = hash
            }
        };
    }

    private static string Cell(GameplayVector3 value) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Math.Floor(value.X / 128):0}:" +
        $"{Math.Floor(value.Y / 128):0}:" +
        $"{Math.Floor(value.Z / 128):0}");
}

public sealed record BulletPathCandidateResult(
    bool Available,
    CameraShotPlan? Shot,
    string? UnavailableReason);

public static class BulletPathShotPlanner
{
    public static BulletPathCandidateResult Create(
        KillEvent kill,
        string demoId,
        string mapName,
        int tickRate,
        SafeCameraVolume? safeVolume)
    {
        if (kill.ShooterPosition is null ||
            kill.HitPosition is null ||
            !string.Equals(
                kill.BulletTrajectoryStatus,
                "ExactShooterOriginAndImpact",
                StringComparison.Ordinal))
        {
            return new BulletPathCandidateResult(
                false,
                null,
                "BULLET_PATH_EXACT_TRAJECTORY_UNAVAILABLE");
        }
        if (tickRate <= 0)
            return new BulletPathCandidateResult(
                false,
                null,
                "BULLET_PATH_TICK_RATE_INVALID");
        GameplayVector3 origin = kill.ShooterPosition;
        GameplayVector3 impact = kill.HitPosition;
        if (safeVolume is null ||
            !safeVolume.Contains(origin) ||
            !safeVolume.Contains(impact))
        {
            return new BulletPathCandidateResult(
                false,
                null,
                "BULLET_PATH_OUTSIDE_SAFE_VOLUME");
        }
        double duration = Math.Clamp(
            origin.DistanceTo(impact) / 2200,
            0.25,
            0.65);
        long endTick = kill.Tick + Math.Max(
            1,
            (long)Math.Round(duration * tickRate));
        CameraShotPlan shot = new()
        {
            Id = $"camera-bullet-{demoId}-{kill.Tick}",
            Type = CameraShotType.BulletPath,
            Family = CameraShotFamily.BulletPath,
            DemoId = demoId,
            StartTick = kill.Tick,
            EndTick = endTick,
            TargetDurationSeconds = duration,
            Keyframes =
            [
                Keyframe(0, origin, impact),
                Keyframe(duration, impact, impact)
            ],
            TargetPoints =
            [
                new CameraTargetPoint(0, impact, []),
                new CameraTargetPoint(duration, impact, [])
            ],
            FovCurve =
            [
                new CameraFovPoint(0, 82),
                new CameraFovPoint(duration, 82)
            ],
            FovStart = 82,
            FovEnd = 82,
            FramingIntent = "verified bullet direction accent",
            SafetyVolume = safeVolume,
            PreviewRequired = true,
            RequiresHighFpsCapture = true,
            FallbackShotId = string.Empty,
            FallbackChain = [CameraShotFamily.PlayerPov],
            Warnings = []
        };
        return new BulletPathCandidateResult(
            true,
            CameraShotSignatureBuilder.Attach(shot, mapName),
            null);
    }

    private static CameraKeyframe Keyframe(
        double time,
        GameplayVector3 position,
        GameplayVector3 target)
    {
        double x = target.X - position.X;
        double y = target.Y - position.Y;
        double z = target.Z - position.Z;
        double horizontal = Math.Sqrt(x * x + y * y);
        return new CameraKeyframe
        {
            TimeSeconds = time,
            Position = position,
            Rotation = new GameplayVector3(
                -Math.Atan2(z, Math.Max(0.000001, horizontal)) * 180 / Math.PI,
                Math.Atan2(y, x) * 180 / Math.PI,
                0),
            Fov = 82
        };
    }
}
