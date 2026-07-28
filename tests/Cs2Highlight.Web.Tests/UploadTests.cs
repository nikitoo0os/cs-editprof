using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Http;

namespace Cs2Highlight.Web.Tests;

public sealed class UploadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"web-upload-{Guid.NewGuid():N}");

    [Fact]
    public async Task StreamsMultipleFilesAndDetectsDuplicateSha256()
    {
        DemoUploadService service = CreateService(maximumFiles: 3);
        IFormFile first = Form("first.dem", 2048, 7);
        IFormFile duplicate = Form(@"..\unsafe.dem", 2048, 7);

        IReadOnlyList<StoredUpload> result = await service.SaveAsync(
            Guid.NewGuid().ToString("N"), [first, duplicate], CancellationToken.None);

        Assert.False(result[0].Duplicate);
        Assert.True(result[1].Duplicate);
        Assert.Equal(result[0].Sha256, result[1].Sha256);
        Assert.Equal("unsafe.dem", result[1].OriginalFileName);
        Assert.DoesNotContain("unsafe", result[0].StoredPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsTooManyAndOversizedFiles()
    {
        DemoUploadService service = CreateService(maximumFiles: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(Guid.NewGuid().ToString("N"),
                [Form("a.dem", 2048, 1), Form("b.dem", 2048, 2)], CancellationToken.None));
    }

    private DemoUploadService CreateService(int maximumFiles) =>
        new(
            new GenerationStorage(new StorageOptions { Root = root }),
            new UploadOptions
            {
                MaximumFilesPerGeneration = maximumFiles,
                MaximumFileSizeBytes = 4096,
                MaximumTotalSizeBytes = 8192,
                MinimumDemoSizeBytes = 1,
                MinimumFreeDiskSpaceBytes = 0
            });

    private static FormFile Form(string name, int size, byte value)
    {
        byte[] bytes = Enumerable.Repeat(value, size).ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "DemoFiles", name);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
