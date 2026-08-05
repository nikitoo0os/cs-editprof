using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cs2Highlight.Music;

namespace Cs2Highlight.Web.Services;

public sealed record StoredAutomaticCameraCalibration(
    string SchemaVersion,
    string MapName,
    string HlaeVersion,
    DateTimeOffset UpdatedAt,
    MapCameraProfile Profile);

public sealed class AutomaticCameraCalibrationStore(
    CinematicCameraRuntimeOptions options,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<MapCameraProfile?> LoadAsync(
        string mapName,
        string hlaeVersion,
        CancellationToken cancellationToken)
    {
        string path = PathFor(mapName, hlaeVersion);
        if (!File.Exists(path))
            return null;
        try
        {
            StoredAutomaticCameraCalibration? stored =
                JsonSerializer.Deserialize<StoredAutomaticCameraCalibration>(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    Json);
            if (stored is null ||
                !string.Equals(
                    stored.MapName,
                    mapName,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    stored.HlaeVersion,
                    hlaeVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return stored.Profile;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task MergeAcceptedAsync(
        string mapName,
        string hlaeVersion,
        IReadOnlyList<CameraShotPlan> shots,
        CancellationToken cancellationToken)
    {
        CameraShotPlan[] accepted = shots
            .Where(value =>
                value.AutomaticCalibration &&
                value.SafetyVolume is not null &&
                value.Keyframes.Count > 0)
            .ToArray();
        if (accepted.Length == 0)
            return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            MapCameraProfile? existing = await LoadAsync(
                mapName,
                hlaeVersion,
                cancellationToken);
            SafeCameraVolume[] volumes = (existing?.SafeVolumes ?? [])
                .Concat(accepted.Select(value => value.SafetyVolume!))
                .GroupBy(VolumeKey, StringComparer.Ordinal)
                .Select(value => value.First())
                .ToArray();
            EstablishingCameraPreset[] presets =
                (existing?.EstablishingShots ?? [])
                .Concat(accepted.Select(value =>
                    new EstablishingCameraPreset(
                        "accepted-" +
                            (value.Signature?.DeterministicHash ?? value.Id),
                        value.Keyframes)))
                .GroupBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.First())
                .ToArray();
            MapCameraProfile profile = new()
            {
                MapName = mapName,
                SafeVolumes = volumes,
                EstablishingShots = presets,
                RestrictedVolumes = [],
                ManuallyVerified = false,
                AutomaticallyCalibrated = true
            };
            StoredAutomaticCameraCalibration stored = new(
                "1.0",
                mapName,
                hlaeVersion,
                timeProvider.GetUtcNow(),
                profile);
            string path = PathFor(mapName, hlaeVersion);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") +
                ".tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(stored, Json),
                    cancellationToken);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string PathFor(string mapName, string hlaeVersion)
    {
        string root = Path.GetFullPath(options.CalibrationRoot);
        string key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(mapName + "|" + hlaeVersion)))
            .ToLowerInvariant();
        return Path.Combine(root, key + ".json");
    }

    private static string VolumeKey(SafeCameraVolume volume) =>
        $"{Math.Round(volume.Minimum.X / 32)}:" +
        $"{Math.Round(volume.Minimum.Y / 32)}:" +
        $"{Math.Round(volume.Minimum.Z / 32)}:" +
        $"{Math.Round(volume.Maximum.X / 32)}:" +
        $"{Math.Round(volume.Maximum.Y / 32)}:" +
        $"{Math.Round(volume.Maximum.Z / 32)}";

    public void Dispose() => gate.Dispose();
}
