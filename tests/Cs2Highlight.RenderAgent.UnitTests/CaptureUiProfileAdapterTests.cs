using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class CaptureUiProfileAdapterTests
{
    [Fact]
    public void PovCombatRestoresWeaponAndHidesDemoOverlay()
    {
        IReadOnlyList<string> commands =
            CaptureUiProfileAdapter.GetCommands(CapturePresentationMode.PovCombat);

        Assert.Contains("cl_showdemooverlay 0", commands);
        Assert.Contains("cl_drawhud 1", commands);
        Assert.Contains("r_drawviewmodel 1", commands);
        Assert.Contains("r_show_build_info 0", commands);
        Assert.Contains("cl_trueview_show_status 0", commands);
        Assert.DoesNotContain("demoui", commands);
        List<string> ordered = commands.ToList();
        Assert.True(
            ordered.LastIndexOf("r_drawviewmodel 1") >
            ordered.IndexOf("r_drawviewmodel 1"));
    }

    [Theory]
    [InlineData(CapturePresentationMode.CinematicBroll)]
    [InlineData(CapturePresentationMode.ThirdPersonAction)]
    [InlineData(CapturePresentationMode.EstablishingShot)]
    public void NonPovModeResetsThenHidesWeapon(CapturePresentationMode mode)
    {
        IReadOnlyList<string> commands = CaptureUiProfileAdapter.GetCommands(mode);

        Assert.Contains("cl_showdemooverlay 0", commands);
        Assert.Equal("r_drawviewmodel 1", commands[5]);
        Assert.Equal("r_drawviewmodel 0", commands[^1]);
        Assert.Contains("r_show_build_info 0", commands);
        Assert.Contains("cl_trueview_show_status 0", commands);
        Assert.DoesNotContain("demoui", commands);
    }
}
