using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cs2Highlight.Web.Domain;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace Cs2Highlight.Web.Services;

public sealed class SteamDemoImportOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "Auto";
    public string BoilerWritterPath { get; set; } = "artifacts/boiler-writter/boiler-writter.exe";
    public string ServerBotNodePath { get; set; } = "node";
    public string ServerBotScriptPath { get; set; } = "tools/steam-gc-bot/resolve.cjs";
    public string ServerBotRefreshTokenFile { get; set; } = "artifacts/steam-gc-bot/refresh-token.txt";
    public int TimeoutSeconds { get; set; } = 90;
    public int MaximumCodesPerGeneration { get; set; } = 10;
}

public readonly record struct SteamMatchShareCode(
    string Code,
    ulong MatchId,
    ulong ReservationId,
    ushort TvPort);

public sealed class SteamDemoImportException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static partial class SteamShareCodeDecoder
{
    private const string Dictionary = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";

    [GeneratedRegex(@"^CSGO(-?[A-Za-z0-9_]{5}){5}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShareCodePattern();

    public static SteamMatchShareCode Decode(string value)
    {
        string code = Normalize(value);
        if (!ShareCodePattern().IsMatch(code))
            throw new SteamDemoImportException("INVALID_MATCH_CODE", "Invalid CS2 match share code.");

        string compact = code.Replace("CSGO", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        BigInteger total = BigInteger.Zero;
        foreach (char character in compact.Reverse())
        {
            int digit = Dictionary.IndexOf(character);
            if (digit < 0)
                throw new SteamDemoImportException("INVALID_MATCH_CODE", "Invalid CS2 match share code.");
            total = total * Dictionary.Length + digit;
        }

        byte[] bytes = total.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 18)
            throw new SteamDemoImportException("INVALID_MATCH_CODE", "Invalid CS2 match share code.");
        byte[] padded = new byte[18];
        Buffer.BlockCopy(bytes, 0, padded, 18 - bytes.Length, bytes.Length);
        ulong matchId = ReadUInt64LittleEndian(padded, 0);
        ulong reservationId = ReadUInt64LittleEndian(padded, 8);
        ushort tvPort = (ushort)((padded[17] << 8) | padded[16]);
        if (matchId == 0 || reservationId == 0 || tvPort == 0)
            throw new SteamDemoImportException("INVALID_MATCH_CODE", "Invalid CS2 match share code.");
        return new SteamMatchShareCode(code, matchId, reservationId, tvPort);
    }

    public static string Normalize(string value) =>
        (value ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

    private static ulong ReadUInt64LittleEndian(byte[] bytes, int offset)
    {
        ulong result = 0;
        for (int index = 7; index >= 0; index--)
            result = (result << 8) | bytes[offset + index];
        return result;
    }
}

public static partial class SteamReplayUrlPolicy
{
    private static readonly TimeSpan CertainExpiryAge = TimeSpan.FromDays(45);

    [GeneratedRegex(@"_(?<timestamp>\d{10})\.dem\.bz2$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReplayTimestampPattern();

    public static bool IsCertainlyExpired(string url, DateTimeOffset now)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        Match match = ReplayTimestampPattern().Match(uri.AbsolutePath);
        if (!match.Success ||
            !long.TryParse(match.Groups["timestamp"].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long timestamp))
            return false;
        try
        {
            DateTimeOffset replayTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return now - replayTime > CertainExpiryAge;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static bool IsAllowedValveReplayUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" &&
        uri.Host.EndsWith(".valve.net", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/730/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".dem.bz2", StringComparison.OrdinalIgnoreCase);
}

public sealed class SteamDemoImportService(
    GenerationStorage storage,
    UploadOptions uploadOptions,
    SteamDemoImportOptions options,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StoredUpload>> ImportAsync(
        string publicId,
        IReadOnlyList<string> rawCodes,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            throw new SteamDemoImportException("STEAM_IMPORT_DISABLED", "Steam demo import is disabled.");
        if (rawCodes.Count == 0)
            throw new SteamDemoImportException("NO_MATCH_CODES", "Add at least one match share code.");
        if (rawCodes.Count > Math.Min(uploadOptions.MaximumFilesPerGeneration, options.MaximumCodesPerGeneration))
            throw new SteamDemoImportException("TOO_MANY_MATCH_CODES", "Too many match share codes.");

        List<SteamMatchShareCode> codes = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawCode in rawCodes)
        {
            SteamMatchShareCode code = SteamShareCodeDecoder.Decode(rawCode);
            if (!seen.Add(code.Code))
                throw new SteamDemoImportException("DUPLICATE_MATCH_CODES", "The same match share code was entered twice.");
            codes.Add(code);
        }

        string provider = options.Provider.Trim();
        string? serverBotScript = PipelinePathResolver.Resolve(options.ServerBotScriptPath);
        bool serverBotConfigured = serverBotScript is not null && HasServerBotCredentials();
        bool useServerBot = provider.Equals("ServerBot", StringComparison.OrdinalIgnoreCase) ||
                            provider.Equals("Auto", StringComparison.OrdinalIgnoreCase) && serverBotConfigured;
        if (!provider.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("ServerBot", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("LocalSteam", StringComparison.OrdinalIgnoreCase))
            throw new SteamDemoImportException(
                "STEAM_IMPORT_NOT_CONFIGURED", "Unknown Steam demo import provider.");
        if (useServerBot && !serverBotConfigured)
            throw new SteamDemoImportException(
                "STEAM_BOT_NOT_CONFIGURED",
                "Steam server bot is missing its helper script or refresh token.");

        string? helper = null;
        if (!useServerBot)
        {
            helper = PipelinePathResolver.Resolve(options.BoilerWritterPath);
            if (helper is null)
                throw new SteamDemoImportException(
                    "STEAM_IMPORT_NOT_CONFIGURED",
                    "boiler-writter is not configured on the render machine.");
        }

        string uploads = storage.EnsureDirectory(publicId, "uploads");
        DriveInfo drive = new(Path.GetPathRoot(uploads)!);
        if (drive.AvailableFreeSpace < uploadOptions.MinimumFreeDiskSpaceBytes)
            throw new SteamDemoImportException("INSUFFICIENT_DISK_SPACE", "Not enough free disk space for the demo.");

        List<StoredUpload> result = [];
        HashSet<string> hashes = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            IReadOnlyDictionary<string, string>? serverBotUrls = useServerBot
                ? await RunServerBotAsync(serverBotScript!, codes, cancellationToken)
                : null;
            for (int index = 0; index < codes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SteamMatchShareCode code = codes[index];
                string infoPath = Path.Combine(uploads, $".steam-match-{index + 1:D3}-{Guid.NewGuid():N}.info");
                string temporary = Path.Combine(uploads, $".steam-demo-{Guid.NewGuid():N}.dem.tmp");
                string destination = Path.Combine(uploads, $"demo-{index + 1:D3}.dem");
                try
                {
                    string demoUrl;
                    if (serverBotUrls is not null)
                    {
                        if (!serverBotUrls.TryGetValue(code.Code, out demoUrl!))
                            throw new SteamDemoImportException(
                                "STEAM_BOT_FAILED", "Steam bot omitted a requested match.");
                    }
                    else
                    {
                        await RunBoilerWritterAsync(helper!, infoPath, code, cancellationToken);
                        demoUrl = ExtractDemoUrl(await File.ReadAllBytesAsync(infoPath, cancellationToken));
                    }
                    await DownloadAndDecompressAsync(demoUrl, temporary, cancellationToken);
                    StoredUpload stored = await StoreDemoAsync(
                        code, temporary, destination, hashes, cancellationToken);
                    if (!stored.Duplicate)
                        result.Add(stored);
                }
                finally
                {
                    DeleteIfExists(infoPath);
                    DeleteIfExists(temporary);
                }
            }
        }
        catch (SteamDemoImportException)
        {
            foreach (StoredUpload upload in result) DeleteIfExists(upload.StoredPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            foreach (StoredUpload upload in result) DeleteIfExists(upload.StoredPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException)
        {
            foreach (StoredUpload upload in result) DeleteIfExists(upload.StoredPath);
            throw new SteamDemoImportException("STEAM_IMPORT_FAILED", "Steam did not return a downloadable demo.");
        }

        if (result.Count == 0)
            throw new SteamDemoImportException("ALL_MATCHES_DUPLICATE", "All imported demos are already in this generation.");
        return result;
    }

    private bool HasServerBotCredentials() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_STEAM_BOT_REFRESH_TOKEN")) ||
        PipelinePathResolver.Resolve(options.ServerBotRefreshTokenFile) is not null;

    private async Task<IReadOnlyDictionary<string, string>> RunServerBotAsync(
        string script,
        List<SteamMatchShareCode> codes,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = options.ServerBotNodePath,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(script);
        foreach (SteamMatchShareCode code in codes) startInfo.ArgumentList.Add(code.Code);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_STEAM_BOT_REFRESH_TOKEN")) &&
            PipelinePathResolver.Resolve(options.ServerBotRefreshTokenFile) is { } tokenFile)
            startInfo.Environment["CS2_STEAM_BOT_REFRESH_TOKEN_FILE"] = tokenFile;
        startInfo.Environment["CS2_STEAM_BOT_REQUEST_TIMEOUT_MS"] =
            (Math.Max(10, options.TimeoutSeconds) * 1000).ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new SteamDemoImportException(
                    "STEAM_BOT_NOT_CONFIGURED", "Could not start the Steam server bot.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new SteamDemoImportException(
                "STEAM_BOT_NOT_CONFIGURED", $"Could not start Node.js: {exception.Message}");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(
            Math.Max(30, options.TimeoutSeconds + codes.Count * 30)));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SteamDemoImportException(
                "STEAM_IMPORT_TIMEOUT", "Steam server bot did not answer in time.");
        }

        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
        {
            SteamBotError? parsedError = ParseServerBotError(error);
            string code = parsedError?.Code switch
            {
                "MATCH_NOT_FOUND" or "DEMO_URL_NOT_FOUND" or "INVALID_MATCH_CODE" or
                "STEAM_BOT_NOT_CONFIGURED" or "STEAM_BOT_AUTH_FAILED" or
                "STEAM_BOT_GC_UNAVAILABLE" => parsedError.Code,
                _ => "STEAM_BOT_FAILED"
            };
            throw new SteamDemoImportException(
                code, parsedError?.Message ?? "Steam server bot failed to resolve the match.");
        }

        SteamBotMatch[] responses;
        try
        {
            responses = JsonSerializer.Deserialize<SteamBotMatch[]>(output, WebJson) ?? [];
        }
        catch (JsonException)
        {
            throw new SteamDemoImportException(
                "STEAM_BOT_FAILED", "Steam server bot returned malformed output.");
        }
        if (responses.Length != codes.Count)
            throw new SteamDemoImportException(
                "STEAM_BOT_FAILED", "Steam server bot returned an incomplete response.");

        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (SteamBotMatch response in responses)
        {
            if (!codes.Any(value => value.Code.Equals(response.Code, StringComparison.OrdinalIgnoreCase)) ||
                !SteamReplayUrlPolicy.IsAllowedValveReplayUrl(response.DemoUrl))
                throw new SteamDemoImportException(
                    "STEAM_BOT_FAILED", "Steam server bot returned an invalid replay URL.");
            result[response.Code] = response.DemoUrl;
        }
        return result;
    }

    private static SteamBotError? ParseServerBotError(string stderr)
    {
        foreach (string line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .AsEnumerable().Reverse())
        {
            try
            {
                if (JsonSerializer.Deserialize<SteamBotError>(line, WebJson) is { } error)
                    return error;
            }
            catch (JsonException)
            {
                // Debug output may precede the final structured error.
            }
        }
        return null;
    }

    private async Task RunBoilerWritterAsync(
        string helper,
        string infoPath,
        SteamMatchShareCode code,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = helper,
            WorkingDirectory = Path.GetDirectoryName(helper) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(infoPath);
        startInfo.ArgumentList.Add(code.MatchId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(code.ReservationId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(code.TvPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
            throw new SteamDemoImportException("STEAM_IMPORT_FAILED", "Could not start the Steam match importer.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SteamDemoImportException("STEAM_IMPORT_TIMEOUT", "Steam did not answer in time. Check that Steam is running and logged in.");
        }

        string error = await stderr;
        await stdout;
        if (process.ExitCode == 8)
            throw new SteamDemoImportException(
                "MATCH_NOT_FOUND",
                "Steam Game Coordinator did not find a match for this share code.");
        if (process.ExitCode != 0 || !File.Exists(infoPath))
        {
            string detail = error.Trim();
            throw new SteamDemoImportException(
                "STEAM_IMPORT_FAILED",
                detail.Length > 0 ? detail[..Math.Min(512, detail.Length)] :
                    "Steam could not load this match. The replay may have expired.");
        }
    }

    private async Task DownloadAndDecompressAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        if (SteamReplayUrlPolicy.IsCertainlyExpired(url, timeProvider.GetUtcNow()))
            throw new SteamDemoImportException(
                "DEMO_EXPIRED", "This match replay is older than Valve's replay retention window.");
        HttpClient client = httpClientFactory.CreateClient("steam-demo");
        using HttpResponseMessage response = await GetReplayWithRetryAsync(
            client, url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SteamDemoImportException(
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "DEMO_EXPIRED" : "STEAM_DOWNLOAD_FAILED",
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "This match replay has expired on Valve's servers."
                    : "Valve did not return the match replay.");
        await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using BZip2Stream decompressed = BZip2Stream.Create(network, CompressionMode.Decompress, true);
        await using FileStream output = new(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[128 * 1024];
        long written = 0;
        while (true)
        {
            int read = await decompressed.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            written += read;
            if (written > uploadOptions.MaximumFileSizeBytes)
                throw new SteamDemoImportException("FILE_TOO_LARGE", "The downloaded demo is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (written < uploadOptions.MinimumDemoSizeBytes)
            throw new SteamDemoImportException("INVALID_DEMO", "Valve returned an invalid demo file.");
    }

    private static async Task<HttpResponseMessage> GetReplayWithRetryAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode ||
                    !IsTransientDownloadStatus(response.StatusCode) ||
                    attempt == maximumAttempts)
                    return response;
            }
            catch (HttpRequestException) when (attempt < maximumAttempts)
            {
                // Valve replay hosts occasionally reset a connection while an edge node warms up.
            }

            response?.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))), cancellationToken);
        }

        throw new UnreachableException();
    }

    private static bool IsTransientDownloadStatus(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    private static async Task<StoredUpload> StoreDemoAsync(
        SteamMatchShareCode code,
        string temporary,
        string destination,
        HashSet<string> hashes,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream input = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer.AsSpan(0, read));
        }
        string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        long size = new FileInfo(temporary).Length;
        if (!hashes.Add(sha256))
            return new StoredUpload($"match-{code.Code}.dem", string.Empty, size, sha256, true);
        File.Move(temporary, destination);
        return new StoredUpload($"match-{code.Code}.dem", destination, size, sha256, false);
    }

    private static string ExtractDemoUrl(byte[] payload)
    {
        foreach (ProtoField match in ReadFields(payload).Where(value => value.Number == 4 && value.WireType == 2))
        {
            foreach (ProtoField stats in ReadFields(match.Bytes).Where(value =>
                         (value.Number == 4 || value.Number == 5) && value.WireType == 2))
            {
                ProtoField? map = ReadFields(stats.Bytes)
                    .Where(value => value.Number == 3 && value.WireType == 2 && value.Bytes is not null)
                    .Select(value => (ProtoField?)value)
                    .FirstOrDefault();
                if (!map.HasValue || map.Value.Bytes is null) continue;
                string url = Encoding.UTF8.GetString(map.Value.Bytes);
                if (SteamReplayUrlPolicy.IsAllowedValveReplayUrl(url)) return new Uri(url).ToString();
            }
        }
        throw new SteamDemoImportException("DEMO_URL_NOT_FOUND", "Steam returned match data without a demo URL.");
    }

    private sealed record SteamBotMatch(string Code, string DemoUrl);
    private sealed record SteamBotError(string Code, string Message);

    private static List<ProtoField> ReadFields(byte[] payload)
    {
        List<ProtoField> fields = [];
        int offset = 0;
        while (offset < payload.Length)
        {
            ulong tag = ReadVarint(payload, ref offset);
            int number = checked((int)(tag >> 3));
            int wireType = checked((int)(tag & 7));
            switch (wireType)
            {
                case 0:
                    fields.Add(new ProtoField(number, wireType, ReadVarint(payload, ref offset), []));
                    break;
                case 1:
                    EnsureAvailable(payload, offset, 8);
                    offset += 8;
                    break;
                case 2:
                    ulong length = ReadVarint(payload, ref offset);
                    if (length > int.MaxValue) throw new InvalidDataException("Proto field is too large.");
                    EnsureAvailable(payload, offset, (int)length);
                    fields.Add(new ProtoField(number, wireType, 0,
                        payload.AsSpan(offset, (int)length).ToArray()));
                    offset += (int)length;
                    break;
                case 5:
                    EnsureAvailable(payload, offset, 4);
                    offset += 4;
                    break;
                default:
                    throw new InvalidDataException("Unsupported protobuf wire type.");
            }
        }
        return fields;
    }

    private static ulong ReadVarint(byte[] payload, ref int offset)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= payload.Length) throw new InvalidDataException("Truncated protobuf payload.");
            byte current = payload[offset++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    private static void EnsureAvailable(byte[] payload, int offset, int length)
    {
        if (length < 0 || offset > payload.Length - length)
            throw new InvalidDataException("Truncated protobuf payload.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private static void DeleteIfExists(string path)
    {
        if (path.Length > 0 && File.Exists(path)) File.Delete(path);
    }

    private readonly record struct ProtoField(int Number, int WireType, ulong Varint, byte[] Bytes);
}
