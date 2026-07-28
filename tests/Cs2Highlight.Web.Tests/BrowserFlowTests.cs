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

    [Fact(Timeout = 1_200_000)]
    [Trait("Category", "Stage6BrowserE2E")]
    public async Task MusicDrivenFlowCompletesWithoutManualReloadWhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("CS2_STAGE6_BROWSER_E2E") != "1")
            return;
        string baseUrl = Required("CS2_WEB_BASE_URL").TrimEnd('/');
        string music = Path.GetFullPath(Required("CS2_STAGE6_MUSIC"));
        string[] demos = Required("CS2_STAGE6_DEMOS")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();
        Assert.NotEmpty(demos);
        Assert.All(demos.Append(music), path => Assert.True(File.Exists(path), path));
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        page.SetDefaultTimeout(180_000);

        await page.GotoAsync(baseUrl);
        await page.Locator("#demos").SetInputFilesAsync(demos);
        await page.GetByRole(AriaRole.Button, new() { Name = "Загрузить и проанализировать" })
            .ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/generations/", StringComparison.Ordinal));
        await ClickWhenAvailableAsync(page, "Выбрать игрока");

        string? steamId = Environment.GetEnvironmentVariable("CS2_STAGE6_STEAM_ID");
        ILocator player = string.IsNullOrWhiteSpace(steamId)
            ? page.Locator("input[type=radio][name=SteamId]").First
            : page.Locator($"input[type=radio][name=SteamId][value='{steamId}']");
        await player.CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Продолжить к оплате" })
            .ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Top 3" }).ClickAsync();
        await page.Locator("input[name=EffectPreset][value=Dynamic]").CheckAsync();
        await page.Locator("#selection-form button[type=submit]").ClickAsync();

        await page.Locator("input[type=file][name=MusicFile]").SetInputFilesAsync(music);
        await page.Locator("input[type=checkbox][name=RightsConfirmed]").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Проанализировать музыку" })
            .ClickAsync();
        await ClickWhenAvailableAsync(page, "Музыка и стиль");
        await Assertions.Expect(page.Locator("text=BPM:")).ToHaveCountAsync(1);
        await page.Locator("select[name=MovieStyle]").SelectOptionAsync("Dynamic");
        await page.Locator("select[name=SyncIntensity]").SelectOptionAsync("Expressive");
        await page.Locator("select[name=ColorGrade]").SelectOptionAsync("CinematicCool");
        await page.GetByRole(AriaRole.Button, new() { Name = "Продолжить к оплате" })
            .ClickAsync();
        await Assertions.Expect(page.GetByText("$1.00 USD")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Оплатить $1" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Успешная оплата" }).ClickAsync();

        await page.Locator("#video-result:not([hidden])").WaitForAsync(
            new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 900_000
            });
        await Assertions.Expect(page.Locator("#status"))
            .ToContainTextAsync("Completed", new() { Timeout = 10_000 });
        IDownload download = await page.RunAndWaitForDownloadAsync(async () =>
            await page.GetByRole(AriaRole.Link, new() { Name = "Скачать MP4" }).ClickAsync());
        Assert.EndsWith(".mp4", download.SuggestedFilename, StringComparison.OrdinalIgnoreCase);

        string generationUrl = page.Url;
        string publicId = generationUrl.TrimEnd('/').Split('/').Last();
        await page.GotoAsync($"{baseUrl}/generations/{publicId}/music");
        await page.WaitForURLAsync(url =>
            !url.EndsWith("/music", StringComparison.OrdinalIgnoreCase));
        await Assertions.Expect(page.Locator("form select[name=MovieStyle]")).ToHaveCountAsync(0);
    }

    private static async Task ClickWhenAvailableAsync(IPage page, string label)
    {
        ILocator link = page.GetByRole(AriaRole.Link, new() { Name = label });
        await link.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 180_000
        });
        await link.ClickAsync();
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ??
        throw new InvalidOperationException($"{name} is required.");
}
