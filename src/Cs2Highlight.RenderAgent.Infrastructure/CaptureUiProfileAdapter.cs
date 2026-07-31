using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public static class CaptureUiProfileAdapter
{
    public const string TemplateVersion = "capture-presentation-reset.v4";

    private static readonly string[] CommonResetCommands =
    [
        "mirv_cvar_unhide_all",
        "demo_timescale 1",
        "cl_showdemooverlay 0",
        "cl_draw_only_deathnotices 0",
        "spec_show_xray 0",
        "r_drawviewmodel 1",
        "r_show_build_info 0",
        "cl_trueview_show_status 0",
        "gameui_hide",
        "hideconsole"
    ];

    public static IReadOnlyList<string> GetCommands(CapturePresentationMode mode)
    {
        string[] modeCommands = mode switch
        {
            CapturePresentationMode.PovCombat =>
            [
                "cl_drawhud 1",
                "r_drawviewmodel 1"
            ],
            CapturePresentationMode.CinematicBroll or
            CapturePresentationMode.ThirdPersonAction or
            CapturePresentationMode.EstablishingShot =>
            [
                "cl_drawhud 0",
                "r_drawviewmodel 0"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        return [.. CommonResetCommands, .. modeCommands];
    }
}
