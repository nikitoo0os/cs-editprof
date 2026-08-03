using Cs2Highlight.Analysis;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class WeaponCatalogTests
{
    [Theory]
    [InlineData("AK-47", "ak47")]
    [InlineData("weapon_m4a4", "m4a4")]
    [InlineData("M4A1-S", "m4a1_silencer")]
    [InlineData("weapon_m4a1_silencer_off", "m4a1_silencer")]
    [InlineData("CZ75 Auto", "cz75a")]
    [InlineData("knife_karambit", "knife")]
    [InlineData("Galil AR", "galilar")]
    [InlineData("Five-SeveN", "fiveseven")]
    [InlineData("MP5-SD", "mp5sd")]
    [InlineData("Sawed-Off", "sawedoff")]
    [InlineData("weapon_flashbang", "flashbang")]
    public void CanonicalizeRecognizesDemoWeaponNames(string value, string expected)
    {
        WeaponCatalog catalog = new();

        Assert.Equal(expected, catalog.Canonicalize(value));
        Assert.NotEqual("unknown", catalog.Resolve(value).Code);
        Assert.EndsWith($"{expected}.svg", catalog.Resolve(value).IconPath);
    }

    [Fact]
    public void M4VariantsKeepTheirOwnIdentity()
    {
        WeaponCatalog catalog = new();

        Assert.Equal("M4A4", catalog.Resolve("M4A4").DisplayName);
        Assert.Equal("M4A1", catalog.Resolve("M4A1").DisplayName);
        Assert.NotEqual(catalog.Resolve("M4A4").IconPath, catalog.Resolve("M4A1").IconPath);
    }
}
