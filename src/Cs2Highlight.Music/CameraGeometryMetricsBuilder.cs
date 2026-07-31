using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public static class CameraGeometryMetricsBuilder
{
    public static CameraPreviewMetrics Enrich(
        CameraShotPlan shot,
        CameraPreviewMetrics mediaMetrics)
    {
        if (shot.Family == CameraShotFamily.PlayerPov)
            return mediaMetrics;
        if (shot.Keyframes.Count == 0 || shot.TargetPoints.Count == 0)
        {
            return mediaMetrics with
            {
                SubjectVisibleRatio = 0,
                SubjectCenterDistance = 1,
                SubjectLossDurationSeconds = shot.TargetDurationSeconds,
                GroupCoverageRatio = 0,
                CameraInsideGeometry = true
            };
        }
        List<double> centerDistances = [];
        List<double> subjectDistances = [];
        int visible = 0;
        int wallIntersections = 0;
        int teleports = 0;
        double maximumAngularVelocity = 0;
        double maximumFovVelocity = 0;
        for (int index = 0; index < shot.Keyframes.Count; index++)
        {
            CameraKeyframe camera = shot.Keyframes[index];
            CameraTargetPoint target = NearestTarget(
                shot.TargetPoints,
                camera.TimeSeconds);
            GameplayVector3 expected = LookAt(
                camera.Position,
                target.Position);
            double yawError = AngleDelta(camera.Rotation.Y, expected.Y);
            double pitchError = AngleDelta(camera.Rotation.X, expected.X);
            double angularError = Math.Sqrt(
                yawError * yawError + pitchError * pitchError);
            double normalizedCenter = angularError /
                Math.Max(1, camera.Fov / 2);
            centerDistances.Add(normalizedCenter);
            double distance = camera.Position.DistanceTo(target.Position);
            subjectDistances.Add(distance);
            if (normalizedCenter <= 0.82 && distance is >= 18 and <= 1200)
                visible++;
            if (shot.SafetyVolume is null ||
                !shot.SafetyVolume.Contains(camera.Position))
                wallIntersections++;
            if (index == 0)
                continue;
            CameraKeyframe previous = shot.Keyframes[index - 1];
            double seconds = camera.TimeSeconds - previous.TimeSeconds;
            if (seconds <= 0)
            {
                teleports++;
                continue;
            }
            double speed = previous.Position.DistanceTo(camera.Position) /
                seconds;
            if (speed > 1600)
                teleports++;
            double angular = Math.Sqrt(
                Math.Pow(
                    AngleDelta(previous.Rotation.X, camera.Rotation.X),
                    2) +
                Math.Pow(
                    AngleDelta(previous.Rotation.Y, camera.Rotation.Y),
                    2));
            maximumAngularVelocity = Math.Max(
                maximumAngularVelocity,
                angular / seconds);
            maximumFovVelocity = Math.Max(
                maximumFovVelocity,
                Math.Abs(camera.Fov - previous.Fov) / seconds);
        }
        double visibleRatio = visible / (double)shot.Keyframes.Count;
        double lossDuration = (1 - visibleRatio) *
            shot.TargetDurationSeconds;
        double minimumDistance = subjectDistances.DefaultIfEmpty(0).Min();
        double averageDistance = subjectDistances.DefaultIfEmpty(0).Average();
        double scale = averageDistance <= 0
            ? 0
            : Math.Clamp(96 / averageDistance, 0, 1);
        return mediaMetrics with
        {
            SubjectVisibleRatio = visibleRatio,
            SubjectCenterDistance = centerDistances.Average(),
            HeadRoom = 0.12,
            LeadRoom = shot.Family is
                CameraShotFamily.SideTracking or
                CameraShotFamily.RearTracking or
                CameraShotFamily.FrontTracking
                    ? 0.18
                    : 0.12,
            SubjectScale = scale,
            SubjectClippingRatio = minimumDistance < 18 ? 1 : 0,
            SubjectLossDurationSeconds = lossDuration,
            GroupCoverageRatio = shot.Family == CameraShotFamily.GroupWide
                ? shot.SubjectIds.Count >= 2 ? visibleRatio : 0
                : null,
            WallIntersectionCount = wallIntersections,
            CameraInsideGeometry = wallIntersections > 0,
            MaximumAngularVelocity = maximumAngularVelocity,
            MaximumFovVelocity = maximumFovVelocity,
            ExcessiveMotionRatio = maximumAngularVelocity > 180 ? 1 : 0,
            CameraTeleportCount = teleports,
            ModelClippingRatio = shot.Family == CameraShotFamily.WeaponDetail &&
                minimumDistance < 28
                    ? 1
                    : 0
        };
    }

    private static CameraTargetPoint NearestTarget(
        IReadOnlyList<CameraTargetPoint> targets,
        double time)
    {
        CameraTargetPoint selected = targets[0];
        double distance = Math.Abs(selected.TimeSeconds - time);
        for (int index = 1; index < targets.Count; index++)
        {
            double current = Math.Abs(targets[index].TimeSeconds - time);
            if (current < distance)
            {
                selected = targets[index];
                distance = current;
            }
        }
        return selected;
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

    private static double AngleDelta(double left, double right)
    {
        double delta = (left - right) % 360;
        if (delta > 180)
            delta -= 360;
        if (delta < -180)
            delta += 360;
        return Math.Abs(delta);
    }
}
