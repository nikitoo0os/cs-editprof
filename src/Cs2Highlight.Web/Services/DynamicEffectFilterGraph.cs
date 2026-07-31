using System.Globalization;
using System.Text;
using Cs2Highlight.Music;

namespace Cs2Highlight.Web.Services;

public enum EffectRenderStage
{
    Time = 30,
    Zoom = 40,
    Motion = 50,
    Temporal = 60,
    Distortion = 70,
    Color = 80,
    Accent = 90,
    Transition = 110
}

public sealed record FfmpegFilterFragment(
    string CueId,
    VideoEffectType EffectType,
    EffectRenderStage Stage,
    string Filter);

public sealed record EffectRenderContext(
    int Width,
    int Height,
    int Fps,
    double DurationSeconds);

public interface IVideoEffectRenderer
{
    VideoEffectType EffectType { get; }
    EffectCapabilityRequirement Requirements { get; }
    FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context);
}

public sealed record DynamicFfmpegFilterGraph(
    string FilterComplex,
    string VideoOutputLabel,
    string AudioOutputLabel,
    IReadOnlyList<FfmpegFilterFragment> Fragments);

public interface IDynamicEffectFilterGraphBuilder
{
    DynamicFfmpegFilterGraph Build(
        string videoInputLabel,
        string audioInputLabel,
        double sourceDurationSeconds,
        DynamicEffectPlan plan,
        TimeWarpPlan? timeWarp,
        VideoOutputOptions output,
        string audioFilters,
        IReadOnlyList<string>? postEffectVideoFilters = null,
        double? targetDurationSeconds = null);
}

public sealed class DynamicEffectFilterGraphBuilder : IDynamicEffectFilterGraphBuilder
{
    private readonly Dictionary<VideoEffectType, IVideoEffectRenderer> renderers;

    public DynamicEffectFilterGraphBuilder()
    {
        IVideoEffectRenderer[] values =
        [
            new ZoomEffectRenderer(VideoEffectType.SmoothZoom),
            new ZoomEffectRenderer(VideoEffectType.PunchZoom),
            new ZoomEffectRenderer(VideoEffectType.CrashZoom),
            new ZoomEffectRenderer(VideoEffectType.ZoomPulse),
            new ZoomEffectRenderer(VideoEffectType.OffsetZoom),
            new ShakeEffectRenderer(VideoEffectType.MicroShake),
            new ShakeEffectRenderer(VideoEffectType.RecoilShake),
            new TemporalEffectRenderer(VideoEffectType.DirectionalMotionBlur),
            new TemporalEffectRenderer(VideoEffectType.ZoomBlur),
            new TemporalEffectRenderer(VideoEffectType.FrameEcho),
            new HitStopEffectRenderer(),
            new RgbSplitEffectRenderer(),
            new LensWarpEffectRenderer(),
            new RollBurstEffectRenderer(),
            new AccentEffectRenderer(VideoEffectType.FlashAccent),
            new AccentEffectRenderer(VideoEffectType.VignettePulse),
            new TransitionEffectRenderer(VideoEffectType.HardCut),
            new TransitionEffectRenderer(VideoEffectType.FadeTransition),
            new TransitionEffectRenderer(VideoEffectType.FlashCut),
            new TransitionEffectRenderer(VideoEffectType.WhipPan),
            new TransitionEffectRenderer(VideoEffectType.WhipZoom)
        ];
        renderers = values.ToDictionary(value => value.EffectType);
    }

    public DynamicFfmpegFilterGraph Build(
        string videoInputLabel,
        string audioInputLabel,
        double sourceDurationSeconds,
        DynamicEffectPlan plan,
        TimeWarpPlan? timeWarp,
        VideoOutputOptions output,
        string audioFilters,
        IReadOnlyList<string>? postEffectVideoFilters = null,
        double? targetDurationSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoInputLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioInputLabel);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceDurationSeconds, 0);
        ArgumentNullException.ThrowIfNull(plan);
        timeWarp ??= new TimeWarpPlan(
            1,
            [new TimeWarpSegment(0, sourceDurationSeconds, 1)],
            false,
            []);
        double outputDuration = targetDurationSeconds ??
            TimeWarpMath.CoveredOutputDuration(
                timeWarp,
                sourceDurationSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            outputDuration,
            0);
        EffectRenderContext context = new(
            output.Width,
            output.Height,
            output.Fps,
            outputDuration);
        FfmpegFilterFragment[] fragments = plan.Effects
            .Where(value => value.Type != VideoEffectType.SpeedRamp)
            .Select(value =>
            {
                if (!renderers.TryGetValue(value.Type, out IVideoEffectRenderer? renderer))
                {
                    throw new InvalidOperationException(
                        $"EFFECT_FILTER_GRAPH_FAILED: no renderer for {value.Type}.");
                }
                return renderer.Build(value, context);
            })
            .OrderBy(value => value.Stage)
            .ThenBy(value => plan.Effects
                .First(effect => effect.Id == value.CueId).StartSeconds)
            .ThenBy(value => value.CueId, StringComparer.Ordinal)
            .ToArray();

        StringBuilder graph = new();
        graph.Append('[').Append(videoInputLabel).Append(']')
            .Append("scale=").Append(output.Width).Append(':').Append(output.Height)
            .Append(":force_original_aspect_ratio=decrease,pad=")
            .Append(output.Width).Append(':').Append(output.Height)
            .Append(":(ow-iw)/2:(oh-ih)/2,fps=").Append(output.Fps)
            .Append(",settb=AVTB,setpts=PTS-STARTPTS[effect_base_v];");
        BuildTimeWarp(
            graph,
            "effect_base_v",
            audioInputLabel,
            audioFilters,
            timeWarp,
            sourceDurationSeconds);

        string current = "effect_warped_v";
        int labelIndex = 0;
        foreach (FfmpegFilterFragment fragment in fragments.Where(value =>
                     value.Stage == EffectRenderStage.Time &&
                     value.EffectType == VideoEffectType.HitStop))
        {
            EffectCue cue = plan.Effects.First(value => value.Id == fragment.CueId);
            string next = $"effect_hitstop_{labelIndex++}";
            BuildHitStop(graph, current, next, cue, context);
            current = next;
        }

        foreach (FfmpegFilterFragment fragment in fragments.Where(value =>
                     !(value.Stage == EffectRenderStage.Time &&
                       value.EffectType == VideoEffectType.HitStop)))
        {
            if (string.IsNullOrWhiteSpace(fragment.Filter))
                continue;
            string next = $"effect_stage_{labelIndex++}";
            graph.Append('[').Append(current).Append(']')
                .Append(fragment.Filter)
                .Append('[').Append(next).Append("];");
            current = next;
        }

        string finalFilters = string.Join(
            ',',
            (postEffectVideoFilters ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Append($"fps={output.Fps}")
                .Append(
                    "tpad=stop_mode=clone:stop_duration=" +
                    Number(outputDuration))
                .Append("trim=duration=" + Number(outputDuration))
                .Append("setpts=PTS-STARTPTS")
                .Append("format=yuv420p"));
        graph.Append('[').Append(current).Append(']')
            .Append(finalFilters)
            .Append("[effect_video];")
            .Append("[effect_warped_a]apad=whole_dur=")
            .Append(Number(outputDuration))
            .Append(",atrim=duration=")
            .Append(Number(outputDuration))
            .Append(",asetpts=PTS-STARTPTS[effect_audio]");
        return new DynamicFfmpegFilterGraph(
            graph.ToString(),
            "effect_video",
            "effect_audio",
            fragments);
    }

    private static void BuildTimeWarp(
        StringBuilder graph,
        string videoInput,
        string audioInput,
        string audioFilters,
        TimeWarpPlan plan,
        double sourceDuration)
    {
        TimeWarpSegment[] segments = plan.Segments.Count == 0
            ? [new TimeWarpSegment(0, sourceDuration, plan.BaseSpeedFactor)]
            : plan.Segments.OrderBy(value => value.SourceStartSeconds).ToArray();
        if (!plan.UsesLocalRamp || segments.Length == 1)
        {
            double speed = Math.Clamp(plan.BaseSpeedFactor, 0.5, 2);
            graph.Append('[').Append(videoInput).Append("]setpts=PTS/")
                .Append(Number(speed)).Append("[effect_warped_v];")
                .Append('[').Append(audioInput)
                .Append("]asetpts=PTS-STARTPTS,");
            if (Math.Abs(speed - 1) > 0.000001)
                graph.Append("atempo=").Append(Number(speed)).Append(',');
            graph.Append(audioFilters);
            graph.Append("[effect_warped_a];");
            return;
        }

        graph.Append('[').Append(videoInput).Append("]split=")
            .Append(segments.Length);
        for (int index = 0; index < segments.Length; index++)
            graph.Append("[effect_warp_v").Append(index).Append(']');
        graph.Append(';').Append('[').Append(audioInput).Append(']')
            .Append("asplit=").Append(segments.Length);
        for (int index = 0; index < segments.Length; index++)
            graph.Append("[effect_warp_a").Append(index).Append(']');
        graph.Append(';');
        for (int index = 0; index < segments.Length; index++)
        {
            TimeWarpSegment segment = segments[index];
            graph.Append("[effect_warp_v").Append(index)
                .Append("]trim=start=").Append(Number(segment.SourceStartSeconds))
                .Append(":end=").Append(Number(segment.SourceEndSeconds))
                .Append(",setpts=(PTS-STARTPTS)/").Append(Number(segment.Speed))
                .Append("[effect_warp_vo").Append(index).Append("];")
                .Append("[effect_warp_a").Append(index)
                .Append("]atrim=start=").Append(Number(segment.SourceStartSeconds))
                .Append(":end=").Append(Number(segment.SourceEndSeconds))
                .Append(",asetpts=PTS-STARTPTS,atempo=").Append(Number(segment.Speed))
                .Append("[effect_warp_ao").Append(index).Append("];");
        }
        for (int index = 0; index < segments.Length; index++)
            graph.Append("[effect_warp_vo").Append(index).Append(']');
        graph.Append("concat=n=").Append(segments.Length)
            .Append(":v=1:a=0[effect_warped_v];");
        for (int index = 0; index < segments.Length; index++)
            graph.Append("[effect_warp_ao").Append(index).Append(']');
        graph.Append("concat=n=").Append(segments.Length)
            .Append(":v=0:a=1,").Append(audioFilters)
            .Append("[effect_warped_a];");
    }

    private static void BuildHitStop(
        StringBuilder graph,
        string input,
        string output,
        EffectCue cue,
        EffectRenderContext context)
    {
        double start = Math.Clamp(cue.StartSeconds, 0, context.DurationSeconds);
        double end = Math.Clamp(cue.EndSeconds, start, context.DurationSeconds);
        double frame = 1d / context.Fps;
        double holdSourceEnd = Math.Min(context.DurationSeconds, start + frame);
        double pad = Math.Max(0, end - start - frame);
        graph.Append('[').Append(input).Append("]split=3")
            .Append('[').Append(output).Append("_pre]")
            .Append('[').Append(output).Append("_hold]")
            .Append('[').Append(output).Append("_post];")
            .Append('[').Append(output).Append("_pre]trim=start=0:end=")
            .Append(Number(start)).Append(",setpts=PTS-STARTPTS[")
            .Append(output).Append("_pre_o];")
            .Append('[').Append(output).Append("_hold]trim=start=")
            .Append(Number(start)).Append(":end=").Append(Number(holdSourceEnd))
            .Append(",setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=")
            .Append(Number(pad)).Append('[').Append(output).Append("_hold_o];")
            .Append('[').Append(output).Append("_post]trim=start=")
            .Append(Number(end)).Append(",setpts=PTS-STARTPTS[")
            .Append(output).Append("_post_o];")
            .Append('[').Append(output).Append("_pre_o]")
            .Append('[').Append(output).Append("_hold_o]")
            .Append('[').Append(output).Append("_post_o]")
            .Append("concat=n=3:v=1:a=0[").Append(output).Append("];");
    }

    internal static string Enable(EffectCue cue) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"between(t\\,{cue.StartSeconds:0.######}\\,{cue.EndSeconds:0.######})");

    internal static string Pulse(EffectCue cue)
    {
        double duration = Math.Max(0.000001, cue.EndSeconds - cue.StartSeconds);
        double peakOffset = cue.Parameters.GetValueOrDefault(
            "peakOffsetSeconds",
            duration / 2);
        double rise = Math.Clamp(peakOffset, 0.000001, duration);
        double peak = cue.StartSeconds + rise;
        double fall = Math.Max(0.000001, cue.EndSeconds - peak);
        string linear = string.Create(
            CultureInfo.InvariantCulture,
            $"if(lt(t\\,{peak:0.######})\\,(t-{cue.StartSeconds:0.######})/{rise:0.######}\\,({cue.EndSeconds:0.######}-t)/{fall:0.######})");
        return $"if({Enable(cue)}\\,pow(max(0\\,{linear})\\,0.72)\\,0)";
    }

    internal static string Number(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}

internal sealed class ZoomEffectRenderer(
    VideoEffectType effectType) : IVideoEffectRenderer
{
    public VideoEffectType EffectType => effectType;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(effectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        double scale = Math.Clamp(cue.Parameters.GetValueOrDefault("scale", 1.05), 1, 1.15);
        double centerX = Math.Clamp(cue.Parameters.GetValueOrDefault("centerX", 0.5), 0.4, 0.6);
        double centerY = Math.Clamp(cue.Parameters.GetValueOrDefault("centerY", 0.5), 0.4, 0.6);
        string pulse = DynamicEffectFilterGraphBuilder.Pulse(cue);
        string factor = $"1+{DynamicEffectFilterGraphBuilder.Number(scale - 1)}*({pulse})";
        string filter =
            $"scale=w='{context.Width}*({factor})':h='{context.Height}*({factor})':eval=frame," +
            $"crop={context.Width}:{context.Height}:" +
            $"x='(iw-ow)*{DynamicEffectFilterGraphBuilder.Number(centerX)}':" +
            $"y='(ih-oh)*{DynamicEffectFilterGraphBuilder.Number(centerY)}'";
        return new FfmpegFilterFragment(
            cue.Id,
            effectType,
            EffectRenderStage.Zoom,
            filter);
    }
}

internal sealed class ShakeEffectRenderer(
    VideoEffectType effectType) : IVideoEffectRenderer
{
    public VideoEffectType EffectType => effectType;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(effectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        double amplitude = Math.Clamp(
            cue.Parameters.GetValueOrDefault("amplitudePixels", 4),
            0,
            14 * context.Width / 1920d);
        int impulses = Math.Clamp(
            (int)Math.Round(cue.Parameters.GetValueOrDefault("impulses", 3)),
            2,
            5);
        double duration = Math.Max(0.001, cue.EndSeconds - cue.StartSeconds);
        double frequency = impulses * Math.PI * 2 / duration;
        double phase = (cue.Seed % 6283) / 1000d;
        string decay =
            $"max(0\\,1-(t-{DynamicEffectFilterGraphBuilder.Number(cue.StartSeconds)})/{DynamicEffectFilterGraphBuilder.Number(duration)})";
        string activity = $"({DynamicEffectFilterGraphBuilder.Enable(cue)})";
        string pad = DynamicEffectFilterGraphBuilder.Number(Math.Ceiling(amplitude));
        string x =
            $"{pad}+{DynamicEffectFilterGraphBuilder.Number(amplitude)}*{activity}*({decay})*sin({DynamicEffectFilterGraphBuilder.Number(frequency)}*t+{DynamicEffectFilterGraphBuilder.Number(phase)})";
        string y =
            $"{pad}+{DynamicEffectFilterGraphBuilder.Number(amplitude * 0.65)}*{activity}*({decay})*cos({DynamicEffectFilterGraphBuilder.Number(frequency * 0.83)}*t+{DynamicEffectFilterGraphBuilder.Number(phase)})";
        return new FfmpegFilterFragment(
            cue.Id,
            effectType,
            EffectRenderStage.Motion,
            $"pad=iw+2*{pad}:ih+2*{pad}:{pad}:{pad},crop={context.Width}:{context.Height}:x='{x}':y='{y}'");
    }
}

internal sealed class TemporalEffectRenderer(
    VideoEffectType effectType) : IVideoEffectRenderer
{
    public VideoEffectType EffectType => effectType;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(effectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        if (effectType == VideoEffectType.ZoomBlur)
        {
            double sigma = Math.Clamp(
                cue.Parameters.GetValueOrDefault("sigma", 6),
                1,
                12);
            return new FfmpegFilterFragment(
                cue.Id,
                effectType,
                EffectRenderStage.Temporal,
                $"gblur=sigma={DynamicEffectFilterGraphBuilder.Number(sigma)}:steps=2:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'");
        }
        int frames = Math.Clamp(
            (int)Math.Round(cue.Parameters.GetValueOrDefault("frames", 3)),
            2,
            effectType == VideoEffectType.FrameEcho ? 5 : 8);
        string weights = effectType == VideoEffectType.FrameEcho
            ? frames switch
            {
                2 => "1 0.25",
                3 => "1 0.25 0.12",
                4 => "1 0.25 0.12 0.06",
                _ => "1 0.25 0.12 0.06 0.03"
            }
            : string.Join(' ', Enumerable.Range(0, frames)
                .Select(index => DynamicEffectFilterGraphBuilder.Number(
                    Math.Pow(0.72, index))));
        return new FfmpegFilterFragment(
            cue.Id,
            effectType,
            EffectRenderStage.Temporal,
            $"tmix=frames={frames}:weights='{weights}':scale=0:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'");
    }
}

internal sealed class HitStopEffectRenderer : IVideoEffectRenderer
{
    public VideoEffectType EffectType => VideoEffectType.HitStop;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(EffectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context) =>
        new(
            cue.Id,
            EffectType,
            EffectRenderStage.Time,
            "split+trim+tpad+concat");
}

internal sealed class RgbSplitEffectRenderer : IVideoEffectRenderer
{
    public VideoEffectType EffectType => VideoEffectType.RgbSplit;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(EffectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        int red = Math.Clamp(
            (int)Math.Round(cue.Parameters.GetValueOrDefault("redOffsetX", 2)),
            -8,
            8);
        int blue = Math.Clamp(
            (int)Math.Round(cue.Parameters.GetValueOrDefault("blueOffsetX", -2)),
            -8,
            8);
        return new FfmpegFilterFragment(
            cue.Id,
            EffectType,
            EffectRenderStage.Color,
            $"rgbashift=rh={red}:bh={blue}:edge=smear:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'");
    }
}

internal sealed class LensWarpEffectRenderer : IVideoEffectRenderer
{
    public VideoEffectType EffectType => VideoEffectType.LensWarpPulse;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(EffectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        double k1 = Math.Clamp(cue.Parameters.GetValueOrDefault("k1", -0.05), -0.12, 0.08);
        return new FfmpegFilterFragment(
            cue.Id,
            EffectType,
            EffectRenderStage.Distortion,
            $"lenscorrection=k1={DynamicEffectFilterGraphBuilder.Number(k1)}:k2=0.01:cx=0.5:cy=0.5:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'");
    }
}

internal sealed class RollBurstEffectRenderer : IVideoEffectRenderer
{
    public VideoEffectType EffectType => VideoEffectType.RollBurst;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(EffectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        double degrees = Math.Clamp(
            cue.Parameters.GetValueOrDefault("angleDegrees", 1),
            -2,
            2);
        string angle =
            $"{DynamicEffectFilterGraphBuilder.Number(degrees * Math.PI / 180)}*({DynamicEffectFilterGraphBuilder.Pulse(cue)})";
        return new FfmpegFilterFragment(
            cue.Id,
            EffectType,
            EffectRenderStage.Motion,
            $"rotate=angle='{angle}':ow=iw:oh=ih:c=black");
    }
}

internal sealed class AccentEffectRenderer(
    VideoEffectType effectType) : IVideoEffectRenderer
{
    public VideoEffectType EffectType => effectType;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(effectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        string filter = effectType switch
        {
            VideoEffectType.FlashAccent =>
                $"eq=brightness='{DynamicEffectFilterGraphBuilder.Number(Math.Clamp(cue.Parameters.GetValueOrDefault("opacity", cue.Intensity), 0, 0.35))}*({DynamicEffectFilterGraphBuilder.Pulse(cue)})':eval=frame",
            _ =>
                $"vignette=angle='PI/2-(PI/2-PI/8)*({DynamicEffectFilterGraphBuilder.Pulse(cue)})':eval=frame"
        };
        return new FfmpegFilterFragment(
            cue.Id,
            effectType,
            EffectRenderStage.Accent,
            filter);
    }
}

internal sealed class TransitionEffectRenderer(
    VideoEffectType effectType) : IVideoEffectRenderer
{
    public VideoEffectType EffectType => effectType;
    public EffectCapabilityRequirement Requirements =>
        DynamicEffectPlanner.Requirements(effectType);

    public FfmpegFilterFragment Build(EffectCue cue, EffectRenderContext context)
    {
        string filter = effectType switch
        {
            VideoEffectType.HardCut => string.Empty,
            VideoEffectType.FadeTransition =>
                $"eq=brightness='-0.08*max(0\\,min(1\\,(t-{DynamicEffectFilterGraphBuilder.Number(cue.StartSeconds)})/{DynamicEffectFilterGraphBuilder.Number(Math.Max(0.001, cue.EndSeconds - cue.StartSeconds))}))':eval=frame:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'",
            VideoEffectType.FlashCut =>
                $"eq=brightness='0.42*({DynamicEffectFilterGraphBuilder.Pulse(cue)})'",
            VideoEffectType.WhipPan =>
                $"gblur=sigma=12:steps=2:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'",
            VideoEffectType.WhipZoom =>
                $"gblur=sigma=8:steps=2:enable='{DynamicEffectFilterGraphBuilder.Enable(cue)}'",
            _ => string.Empty
        };
        return new FfmpegFilterFragment(
            cue.Id,
            effectType,
            EffectRenderStage.Transition,
            filter);
    }
}
