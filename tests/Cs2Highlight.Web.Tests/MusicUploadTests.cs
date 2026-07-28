using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Http;

namespace Cs2Highlight.Web.Tests;

public sealed class MusicUploadTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"music-upload-{Guid.NewGuid():N}");

    [Fact]
    public async Task RightsConfirmationIsMandatory()
    {
        MusicUploadService service = Service();
        FormFile file = File("track.mp3");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SaveAsync(PublicId(), file, false, CancellationToken.None));

        Assert.Equal("MUSIC_RIGHTS_CONFIRMATION_REQUIRED", exception.Message);
    }

    [Fact]
    public async Task UnsupportedExtensionIsRejectedBeforeProbe()
    {
        MusicUploadService service = Service();
        FormFile file = File("track.exe");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SaveAsync(PublicId(), file, true, CancellationToken.None));

        Assert.Equal("MUSIC_UNSUPPORTED_FORMAT", exception.Message);
    }

    [Theory]
    [InlineData("track.mp3")]
    [InlineData("track.wav")]
    [InlineData("track.flac")]
    [InlineData("track.m4a")]
    public async Task AllowedFormatsAreStreamedToServerGeneratedPath(string name)
    {
        MusicUploadService service = Service();
        FormFile file = File(name);

        StoredMusicUpload stored = await service.SaveAsync(
            PublicId(), file, true, CancellationToken.None);

        Assert.True(System.IO.File.Exists(stored.StoredPath));
        Assert.StartsWith("track.", Path.GetFileName(stored.StoredPath), StringComparison.Ordinal);
        Assert.Equal(64, stored.Sha256.Length);
        Assert.Equal(30, stored.Metadata.DurationSeconds);
    }

    [Fact]
    public void TrustedLutCatalogRejectsUnknownAndTraversalPaths()
    {
        string lutRoot = Path.Combine(root, "luts");
        Directory.CreateDirectory(lutRoot);
        TrustedLutCatalog unknown = new(new TrustedLutOptions { Root = lutRoot });
        Assert.Equal(
            "UNKNOWN_LUT_ASSET",
            Assert.Throws<InvalidOperationException>(() => unknown.Resolve("other")).Message);
        TrustedLutCatalog traversal = new(new TrustedLutOptions
        {
            Root = lutRoot,
            Assets = new Dictionary<string, string>
            {
                ["escape"] = "..\\outside.cube"
            }
        });
        Assert.Equal(
            "UNTRUSTED_LUT_ASSET",
            Assert.Throws<InvalidOperationException>(() => traversal.Resolve("escape")).Message);
    }

    [Fact]
    public void TrustedLutCatalogResolvesOnlyWhitelistedCube()
    {
        string lutRoot = Path.Combine(root, "luts");
        Directory.CreateDirectory(lutRoot);
        string path = Path.Combine(lutRoot, "owned.cube");
        System.IO.File.WriteAllText(path, "LUT_3D_SIZE 2");
        TrustedLutCatalog catalog = new(new TrustedLutOptions
        {
            Root = lutRoot,
            Assets = new Dictionary<string, string> { ["owned"] = "owned.cube" }
        });

        Assert.Equal(Path.GetFullPath(path), catalog.Resolve("owned"));
    }

    private MusicUploadService Service() =>
        new(
            new GenerationStorage(new StorageOptions { Root = root }),
            new MusicUploadOptions
            {
                MaximumFileSizeBytes = 1024,
                MinimumFreeDiskSpaceBytes = 0
            },
            new FakeValidator());

    private static FormFile File(string name)
    {
        MemoryStream stream = new([1, 2, 3, 4]);
        return new FormFile(stream, 0, stream.Length, "music", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream"
        };
    }

    private static string PublicId() => Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeValidator : IMusicMediaValidator
    {
        public Task<MusicMediaMetadata> ValidateAsync(
            string path,
            CancellationToken cancellationToken)
        {
            Assert.True(System.IO.File.Exists(path));
            return Task.FromResult(new MusicMediaMetadata(30, 48000, 2, "fixture"));
        }
    }
}
