using System.Net;
using System.Net.Http.Headers;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cs2Highlight.Web.Tests;

public sealed class WebIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"web-integration-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? factory;

    [Fact]
    public async Task HomeAndLivenessAreAvailable()
    {
        using HttpClient client = CreateFactory().CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

        string home = await client.GetStringAsync("/");
        HttpResponseMessage health = await client.GetAsync("/health/live");

        Assert.Contains("Загрузить и проанализировать", home);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task FinalVideoSupportsHttpRangeWithoutExposingPhysicalPath()
    {
        WebApplicationFactory<Program> app = CreateFactory();
        string publicId = Guid.NewGuid().ToString("N");
        string video = Path.Combine(root, "final.mp4");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(video, Enumerable.Range(0, 100).Select(value => (byte)value).ToArray());
        using (IServiceScope scope = app.Services.CreateScope())
        {
            IDbContextFactory<GenerationDbContext> dbFactory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<GenerationDbContext>>();
            await using GenerationDbContext db = await dbFactory.CreateDbContextAsync();
            Generation generation = new()
            {
                PublicId = publicId,
                Status = GenerationStatus.Completed,
                CurrentStage = "Completed",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            generation.Artifacts.Add(new GenerationArtifact
            {
                Type = ArtifactType.FinalVideo,
                FileName = "final-highlights.mp4",
                StoredPath = video,
                ContentType = "video/mp4",
                FileSizeBytes = 100,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.Add(generation);
            await db.SaveChangesAsync();
        }
        using HttpClient client = app.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, $"/generations/{publicId}/video");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        HttpResponseMessage response = await client.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(10, bytes.Length);
        Assert.DoesNotContain(root, response.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        if (factory is not null) return factory;
        Directory.CreateDirectory(root);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:GenerationDb"] =
                        $"Data Source={Path.Combine(root, "test.db")};Pooling=False",
                    ["Storage:Root"] = Path.Combine(root, "storage"),
                    ["Uploads:MinimumFreeDiskSpaceBytes"] = "0"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });
        return factory;
    }

    public void Dispose()
    {
        factory?.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
