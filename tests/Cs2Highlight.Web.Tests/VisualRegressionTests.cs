using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Cs2Highlight.Web.Tests;

public sealed class VisualRegressionTests
{
    [Fact(Timeout = 180_000)]
    [Trait("Category", "Visual")]
    public async Task KeyProductStatesHaveStableDesktopAndMobileScreenshotsWhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("CS2_WEB_VISUAL_TESTS") != "1") return;
        string baseUrl = Required("CS2_WEB_BASE_URL").TrimEnd('/');
        string databasePath = Path.GetFullPath(Required("CS2_WEB_VISUAL_DB"));
        string output = Path.GetFullPath(
            Environment.GetEnvironmentVariable("CS2_WEB_VISUAL_OUTPUT") ??
            Path.Combine("artifacts", "visual-regression"));
        Directory.CreateDirectory(output);

        Dictionary<string, string> fixtures = await SeedAsync(databasePath);
        string demo = Path.Combine(Path.GetTempPath(), $"visual-{Guid.NewGuid():N}.dem");
        await File.WriteAllBytesAsync(demo, new byte[2048]);
        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
                ReducedMotion = ReducedMotion.Reduce
            });

            await CaptureAsync(page, baseUrl, output, "home-empty");
            await page.Locator("#demos").SetInputFilesAsync(demo);
            await page.WaitForTimeoutAsync(50);
            await ScreenshotAsync(page, output, "home-files-selected");
            await Assertions.Expect(page.Locator("[data-file-list] .file-row")).ToHaveCountAsync(1);
            await page.Locator("[data-file-list] .icon-button").ClickAsync();
            await Assertions.Expect(page.GetByText("Файлы ещё не выбраны")).ToBeVisibleAsync();

            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["analysis"]}", output, "analysis-running");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["player"]}/player", output, "player-selection");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["highlights"]}/highlights", output, "highlight-catalog");
            await page.Locator(".highlight-card__label").First.ClickAsync();
            await ScreenshotAsync(page, output, "highlight-selected");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["music-upload"]}/music", output, "music-upload");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["movie-settings"]}/music", output, "music-analyzed");
            await ScreenshotAsync(page, output, "movie-settings");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["timeline"]}/timeline", output, "interactive-timeline");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["checkout"]}/checkout", output, "checkout");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["running"]}", output, "generation-running");
            await page.GetByRole(AriaRole.Button, new() { Name = "Отменить генерацию" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeHiddenAsync();
            await page.EvaluateAsync(
                "() => { const banner = document.querySelector('#connection-banner'); if (banner) banner.hidden = false; }");
            await ScreenshotAsync(page, output, "generation-reconnecting");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["completed"]}", output, "generation-completed");
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["failed"]}", output, "error-state");

            await page.SetViewportSizeAsync(390, 844);
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["timeline"]}/timeline", output, "mobile-interactive-timeline");
            await AssertNoHorizontalOverflowAsync(page, 390);
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["highlights"]}/highlights", output, "mobile-highlight-catalog");
            await AssertNoHorizontalOverflowAsync(page, 390);
            await CaptureAsync(page, $"{baseUrl}/generations/{fixtures["completed"]}", output, "mobile-result");
            await AssertNoHorizontalOverflowAsync(page, 390);

            foreach ((int Width, int Height, string Name) viewport in new[]
            {
                (360, 800, "home-mobile-360"),
                (768, 1024, "home-tablet-768"),
                (1920, 1080, "home-desktop-1920"),
                (2560, 1440, "home-wide-2560")
            })
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                await CaptureAsync(page, baseUrl, output, viewport.Name);
                await AssertNoHorizontalOverflowAsync(page, viewport.Width);
            }
        }
        finally
        {
            File.Delete(demo);
            await CleanupAsync(databasePath, fixtures.Values);
        }
    }

    private static async Task CaptureAsync(
        IPage page,
        string url,
        string output,
        string name)
    {
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator("main")).ToBeVisibleAsync();
        await ScreenshotAsync(page, output, name);
    }

    private static Task<byte[]> ScreenshotAsync(IPage page, string output, string name) =>
        page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(output, $"{name}.png"),
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled
        });

    private static async Task AssertNoHorizontalOverflowAsync(IPage page, int width)
    {
        int scrollWidth = await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth");
        Assert.True(scrollWidth <= width, $"Horizontal overflow: {scrollWidth}px at {width}px.");
    }

    private static async Task<Dictionary<string, string>> SeedAsync(string databasePath)
    {
        DbContextOptions<GenerationDbContext> options =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
        await using GenerationDbContext db = new(options);
        await db.Database.MigrateAsync();

        Dictionary<string, string> ids = [];
        Generation analysis = Create("analysis", GenerationStatus.Analyzing, 16);
        Generation player = Create("player", GenerationStatus.AwaitingPlayerSelection, 24);
        AddDemo(player);
        AddPlayers(player);

        Generation highlights = Create("highlights", GenerationStatus.AwaitingHighlightSelection, 30);
        AddDemo(highlights);
        AddPlayers(highlights);
        highlights.SelectedSteamId = "76561198000000001";
        highlights.SelectedPlayerName = "NightShift";
        AddHighlights(highlights);

        Generation musicUpload = Create("music-upload", GenerationStatus.AwaitingMusicUpload, 38);
        AddDemo(musicUpload);
        musicUpload.SelectedPlayerName = "NightShift";

        Generation movieSettings = Create("movie-settings", GenerationStatus.AwaitingMovieConfiguration, 46);
        AddDemo(movieSettings);
        movieSettings.SelectedPlayerName = "NightShift";
        AddMusic(movieSettings);

        Generation checkout = Create("checkout", GenerationStatus.AwaitingPayment, 52);
        AddDemo(checkout);
        checkout.SelectedSteamId = "76561198000000001";
        checkout.SelectedPlayerName = "NightShift";
        checkout.EstimatedDurationMilliseconds = 52_000;
        AddHighlights(checkout, selected: true);
        AddMusic(checkout);

        Generation timeline = Create("timeline", GenerationStatus.AwaitingPayment, 50);
        AddDemo(timeline);
        timeline.SelectedSteamId = "76561198000000001";
        timeline.SelectedPlayerName = "NightShift";
        timeline.EstimatedDurationMilliseconds = 30_000;
        AddHighlights(timeline, selected: true);
        AddMusic(timeline);

        // This non-worker status keeps the progress dashboard stable while the
        // external test server's real background worker remains enabled.
        Generation running = Create("running", GenerationStatus.ValidatingMoviePlan, 64);
        AddDemo(running);
        running.SelectedPlayerName = "NightShift";
        AddPlayers(running);
        AddHighlights(running, selected: true);
        running.Events.Add(new GenerationEvent
        {
            Stage = "RenderingClips",
            Message = "Rendered 3/5 clips",
            ProgressPercent = 64,
            CreatedAt = DateTimeOffset.UtcNow
        });

        Generation completed = Create("completed", GenerationStatus.Completed, 100);
        AddDemo(completed);
        completed.SelectedPlayerName = "NightShift";
        completed.GenerationCompletedAt = DateTimeOffset.UtcNow;
        completed.Width = 1920;
        completed.Height = 1080;
        completed.Fps = 60;
        AddHighlights(completed, selected: true);

        Generation failed = Create("failed", GenerationStatus.Failed, 61);
        AddDemo(failed);
        failed.SelectedPlayerName = "NightShift";
        failed.ErrorCode = "DEMO_UNSUPPORTED_VERSION";
        failed.ErrorMessage = "Не удалось обработать одну из демок.";

        Generation[] fixtures =
        [
            analysis, player, highlights, musicUpload, movieSettings,
            timeline, checkout, running, completed, failed
        ];
        db.Generations.AddRange(fixtures);
        await db.SaveChangesAsync();
        foreach (Generation fixture in fixtures)
            ids[fixture.CurrentStage] = fixture.PublicId;
        return ids;

        Generation Create(string key, GenerationStatus status, int progress)
        {
            string id = $"visual{Guid.NewGuid():N}";
            ids[key] = id;
            return new Generation
            {
                PublicId = id,
                Status = status,
                CurrentStage = key,
                ProgressPercent = progress,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                EffectPreset = EffectPreset.Dynamic,
                MaximumHighlights = 5
            };
        }
    }

    private static void AddDemo(Generation generation)
    {
        generation.Demos.Add(new GenerationDemo
        {
            OriginalFileName = "match-mirage.dem",
            StoredPath = "fixture.dem",
            FileSizeBytes = 42_000_000,
            Sha256 = Guid.NewGuid().ToString("N"),
            UploadOrder = 1,
            AnalysisStatus = DemoAnalysisStatus.Succeeded,
            MapName = "de_mirage"
        });
    }

    private static void AddPlayers(Generation generation)
    {
        generation.Players.AddRange(
        [
            new GenerationPlayer
            {
                SteamId = "76561198000000001",
                DisplayName = "NightShift",
                DemoCount = 3,
                TotalKills = 84,
                CandidateCount = 17
            },
            new GenerationPlayer
            {
                SteamId = "76561198000000002",
                DisplayName = "silent_ace",
                DemoCount = 3,
                TotalKills = 71,
                CandidateCount = 12
            },
            new GenerationPlayer
            {
                SteamId = "76561198000000003",
                DisplayName = "cyan",
                DemoCount = 2,
                TotalKills = 52,
                CandidateCount = 8
            }
        ]);
    }

    private static void AddHighlights(Generation generation, bool selected = false)
    {
        string[] types = ["TripleKill", "DoubleKill", "SoloKill", "Ace", "QuadKill"];
        for (int index = 0; index < types.Length; index++)
        {
            generation.Highlights.Add(new GenerationHighlight
            {
                HighlightId = $"fixture-{index}",
                SteamId = "76561198000000001",
                Type = types[index],
                MapName = index % 2 == 0 ? "de_mirage" : "de_ancient",
                RoundNumber = 7 + index,
                FirstKillTick = 10_000 + index * 1_000,
                TotalScore = 94 - index * 5,
                CombatScore = 60 - index,
                BeautyScore = 34 - index,
                KillCount = Math.Min(5, index + 1),
                HeadshotCount = index % 3,
                Recommended = index < 3,
                SelectedByUser = selected,
                EstimatedDurationMilliseconds = 10_800,
                WeaponSequenceJson = JsonSerializer.Serialize(new[]
                {
                    new WeaponSequenceSegment(
                        "ak47",
                        "AK-47",
                        "/assets/weapons/ak47.svg",
                        2,
                        false),
                    new WeaponSequenceSegment(
                        "awp",
                        "AWP",
                        "/assets/weapons/awp.svg",
                        1,
                        true)
                }),
                ScoreBreakdownJson = "{}",
                TagsJson = "[\"HEADSHOT\",\"WALLBANG\",\"WEAPON_SWAP\"]",
                KillsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static void AddMusic(Generation generation)
    {
        generation.Music = new GenerationMusic
        {
            OriginalFileName = "neon-drive.mp3",
            StoredPath = "fixture.mp3",
            FileSizeBytes = 8_000_000,
            Sha256 = Guid.NewGuid().ToString("N"),
            ContentType = "audio/mpeg",
            DurationMilliseconds = 168_000,
            SampleRate = 48_000,
            Channels = 2,
            TempoBpm = 132,
            RightsConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task CleanupAsync(string databasePath, IEnumerable<string> publicIds)
    {
        DbContextOptions<GenerationDbContext> options =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
        await using GenerationDbContext db = new(options);
        string[] ids = publicIds.Distinct().ToArray();
        Generation[] generations = await db.Generations
            .Where(value => ids.Contains(value.PublicId))
            .ToArrayAsync();
        db.Generations.RemoveRange(generations);
        await db.SaveChangesAsync();
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ??
        throw new InvalidOperationException($"{name} is required.");
}
