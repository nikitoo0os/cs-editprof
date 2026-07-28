using Microsoft.Playwright;

namespace Cs2Highlight.Web.Tests;

public sealed class BrowserFlowTests
{
    [Fact]
    [Trait("Category", "Browser")]
    public async Task ChromiumCanRenderLandingPageWhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("CS2_WEB_BROWSER_TESTS") != "1") return;
        string? baseUrl = Environment.GetEnvironmentVariable("CS2_WEB_BASE_URL");
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();

        await page.GotoAsync(baseUrl!);

        await Assertions.Expect(page.Locator("h1"))
            .ToContainTextAsync("Соберите лучшие моменты");
        await Assertions.Expect(page.Locator("input[type=file][multiple]")).ToHaveCountAsync(1);
    }
}
