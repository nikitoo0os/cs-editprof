using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed class SteamHistoryOptions
{
    public bool Enabled { get; set; } = true;
    public string WebApiKey { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.steampowered.com/";
    public int MaximumCodesPerSync { get; set; } = 100;
    public int MaximumProbeCodesPerSync { get; set; } = 25;
    public int RequestDelayMilliseconds { get; set; } = 200;
}

public sealed class SteamHistoryException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class SteamHistorySecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector(
        "Cs2Highlight.Web.SteamHistory.AuthenticationCode.v1");

    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new SteamHistoryException(
                "STEAM_HISTORY_SECRET_INVALID",
                "The saved Steam authentication code can no longer be decrypted.");
        }
    }
}

public sealed partial class SteamMatchHistoryApiClient : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly SteamHistoryOptions options;
    private readonly HttpClient client;

    public SteamMatchHistoryApiClient(SteamHistoryOptions options)
    {
        this.options = options;
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CSHighlighter/1.0");
    }

    public async Task<string?> GetNextCodeAsync(
        string steamId64,
        string authenticationCode,
        string knownCode,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            throw new SteamHistoryException("STEAM_HISTORY_DISABLED", "Steam history is disabled.");
        if (string.IsNullOrWhiteSpace(options.WebApiKey))
            throw new SteamHistoryException(
                "STEAM_HISTORY_API_NOT_CONFIGURED", "Steam Web API key is not configured.");

        string baseUrl = options.ApiBaseUrl.TrimEnd('/');
        string requestUrl = $"{baseUrl}/ICSGOPlayers_730/GetNextMatchSharingCode/v1" +
            $"?key={Uri.EscapeDataString(options.WebApiKey.Trim())}" +
            $"&steamid={Uri.EscapeDataString(steamId64)}" +
            $"&steamidkey={Uri.EscapeDataString(authenticationCode)}" +
            $"&knowncode={Uri.EscapeDataString(knownCode)}";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or
                HttpStatusCode.ServiceUnavailable && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new SteamHistoryException(
                    "STEAM_HISTORY_AUTH_INVALID", "Steam rejected the SteamID or authentication code.");
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new SteamHistoryException(
                    "STEAM_HISTORY_KNOWN_CODE_INVALID", "Steam rejected the saved match code.");
            if (!response.IsSuccessStatusCode)
                throw new SteamHistoryException(
                    "STEAM_HISTORY_API_UNAVAILABLE", $"Steam match history returned HTTP {(int)response.StatusCode}.");

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            SteamNextCodeResponse? parsed = await JsonSerializer.DeserializeAsync<SteamNextCodeResponse>(
                body, WebJson, cancellationToken);
            string? next = parsed?.Result?.NextCode?.Trim();
            if (string.IsNullOrWhiteSpace(next) || next.Equals("n/a", StringComparison.OrdinalIgnoreCase))
                return null;
            return SteamShareCodeDecoder.Decode(next).Code;
        }
        throw new SteamHistoryException(
            "STEAM_HISTORY_API_UNAVAILABLE", "Steam match history is temporarily unavailable.");
    }

    public void Dispose() => client.Dispose();

    private sealed record SteamNextCodeResponse(SteamNextCodeResult? Result);
    private sealed record SteamNextCodeResult(string? NextCode);
}

public sealed record SteamMatchProbe(
    string Code,
    string? DemoUrl,
    DateTimeOffset? PlayedAtUtc,
    string? Score,
    string? ErrorCode);

public sealed class SteamServerBotProbeClient(
    SteamDemoImportOptions options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SteamMatchProbe>> ProbeAsync(
        IReadOnlyList<string> rawCodes,
        CancellationToken cancellationToken)
    {
        SteamMatchShareCode[] codes = rawCodes.Select(SteamShareCodeDecoder.Decode).ToArray();
        string? script = PipelinePathResolver.Resolve(options.ServerBotScriptPath);
        bool hasToken = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("CS2_STEAM_BOT_REFRESH_TOKEN")) ||
            PipelinePathResolver.Resolve(options.ServerBotRefreshTokenFile) is not null;
        if (script is null || !hasToken)
            throw new SteamHistoryException(
                "STEAM_BOT_NOT_CONFIGURED", "The Steam server bot is not configured.");

        ProcessStartInfo start = new()
        {
            FileName = options.ServerBotNodePath,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--allow-missing");
        foreach (SteamMatchShareCode code in codes) start.ArgumentList.Add(code.Code);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_STEAM_BOT_REFRESH_TOKEN")) &&
            PipelinePathResolver.Resolve(options.ServerBotRefreshTokenFile) is { } tokenFile)
            start.Environment["CS2_STEAM_BOT_REFRESH_TOKEN_FILE"] = tokenFile;
        start.Environment["CS2_STEAM_BOT_REQUEST_TIMEOUT_MS"] =
            (Math.Max(10, options.TimeoutSeconds) * 1000).ToString(CultureInfo.InvariantCulture);

        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new SteamHistoryException("STEAM_BOT_NOT_CONFIGURED", "Could not start the Steam bot.");
        }
        catch (Win32Exception exception)
        {
            throw new SteamHistoryException("STEAM_BOT_NOT_CONFIGURED", exception.Message);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(
            Math.Max(30, options.TimeoutSeconds + codes.Length * 30)));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SteamHistoryException("STEAM_BOT_GC_UNAVAILABLE", "Steam GC timed out.");
        }

        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
        {
            string code = ParseErrorCode(error) ?? "STEAM_BOT_FAILED";
            throw new SteamHistoryException(code, "Steam bot could not inspect the matches.");
        }

        BotProbeResult[] parsed;
        try { parsed = JsonSerializer.Deserialize<BotProbeResult[]>(output, WebJson) ?? []; }
        catch (JsonException)
        {
            throw new SteamHistoryException("STEAM_BOT_FAILED", "Steam bot returned malformed output.");
        }
        if (parsed.Length != codes.Length)
            throw new SteamHistoryException("STEAM_BOT_FAILED", "Steam bot returned an incomplete response.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        return parsed.Select(value =>
        {
            string? errorCode = value.ErrorCode;
            if (value.DemoUrl is not null && !SteamReplayUrlPolicy.IsAllowedValveReplayUrl(value.DemoUrl))
                errorCode = "DEMO_URL_NOT_FOUND";
            else if (value.DemoUrl is not null && SteamReplayUrlPolicy.IsCertainlyExpired(value.DemoUrl, now))
                errorCode = "DEMO_EXPIRED";
            DateTimeOffset? playedAt = value.PlayedAtUnix is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(value.PlayedAtUnix.Value)
                : null;
            return new SteamMatchProbe(value.Code, value.DemoUrl, playedAt, value.Score, errorCode);
        }).ToArray();
    }

    private static string? ParseErrorCode(string stderr)
    {
        foreach (string line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .AsEnumerable().Reverse())
        {
            try { return JsonSerializer.Deserialize<BotError>(line, WebJson)?.Code; }
            catch (JsonException) { }
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private sealed record BotProbeResult(
        string Code, string? DemoUrl, long? PlayedAtUnix, string? Score, string? ErrorCode);
    private sealed record BotError(string? Code);
}

public sealed record SteamHistorySyncResult(int Added, int Checked, bool Capped);

public sealed partial class SteamHistoryService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    SteamHistorySecretProtector secrets,
    SteamMatchHistoryApiClient api,
    SteamServerBotProbeClient probes,
    SteamHistoryOptions options,
    TimeProvider timeProvider)
{
    [GeneratedRegex(@"^\d{17}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamIdPattern();
    [GeneratedRegex(@"^[A-Za-z0-9]{4,6}(?:-[A-Za-z0-9]{4,6}){2}$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthCodePattern();

    public async Task ConnectAsync(
        string userId,
        string steamId64,
        string authenticationCode,
        string knownCode,
        CancellationToken cancellationToken)
    {
        steamId64 = (steamId64 ?? string.Empty).Trim();
        authenticationCode = (authenticationCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!SteamIdPattern().IsMatch(steamId64) || !ulong.TryParse(steamId64, out _))
            throw new SteamHistoryException("STEAM_HISTORY_STEAM_ID_INVALID", "Invalid SteamID64.");
        if (!AuthCodePattern().IsMatch(authenticationCode))
            throw new SteamHistoryException("STEAM_HISTORY_AUTH_FORMAT_INVALID", "Invalid Steam authentication code.");
        SteamMatchShareCode seed = SteamShareCodeDecoder.Decode(knownCode);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        SteamHistoryConnection? connection = await db.SteamHistoryConnections
            .Include(value => value.Matches)
            .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);
        if (connection is null)
        {
            connection = new SteamHistoryConnection { UserId = userId, CreatedAtUtc = now };
            db.SteamHistoryConnections.Add(connection);
        }
        else if (!connection.SteamId64.Equals(steamId64, StringComparison.Ordinal) ||
                 !connection.CursorShareCode.Equals(seed.Code, StringComparison.OrdinalIgnoreCase))
        {
            db.SteamHistoryMatches.RemoveRange(connection.Matches);
            connection.Matches.Clear();
        }
        connection.SteamId64 = steamId64;
        connection.ProtectedAuthenticationCode = secrets.Protect(authenticationCode);
        connection.CursorShareCode = seed.Code;
        connection.UpdatedAtUtc = now;
        connection.LastErrorCode = null;
        if (!connection.Matches.Any(value => value.ShareCode == seed.Code))
            connection.Matches.Add(CreateMatch(seed, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SteamHistorySyncResult> SyncAsync(string userId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        SteamHistoryConnection connection = await db.SteamHistoryConnections
            .Include(value => value.Matches)
            .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken) ??
            throw new SteamHistoryException("STEAM_HISTORY_NOT_CONNECTED", "Steam history is not connected.");
        string authenticationCode = secrets.Unprotect(connection.ProtectedAuthenticationCode);
        DateTimeOffset now = timeProvider.GetUtcNow();
        int added = 0;
        bool capped = false;
        try
        {
            int maximum = Math.Clamp(options.MaximumCodesPerSync, 1, 500);
            for (int index = 0; index < maximum; index++)
            {
                string? next = await api.GetNextCodeAsync(
                    connection.SteamId64, authenticationCode, connection.CursorShareCode, cancellationToken);
                if (next is null) break;
                SteamMatchShareCode decoded = SteamShareCodeDecoder.Decode(next);
                if (connection.Matches.All(value => !value.ShareCode.Equals(decoded.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    connection.Matches.Add(CreateMatch(decoded, now));
                    added++;
                }
                connection.CursorShareCode = decoded.Code;
                connection.UpdatedAtUtc = now;
                if (options.RequestDelayMilliseconds > 0)
                    await Task.Delay(options.RequestDelayMilliseconds, cancellationToken);
                if (index == maximum - 1) capped = true;
            }
            await db.SaveChangesAsync(cancellationToken);

            SteamHistoryMatch[] toProbe = connection.Matches
                .Where(value => value.Availability == SteamReplayAvailability.Unknown)
                .OrderByDescending(value => value.Id)
                .Take(Math.Clamp(options.MaximumProbeCodesPerSync, 1, 100))
                .ToArray();
            if (toProbe.Length > 0)
            {
                IReadOnlyList<SteamMatchProbe> results = await probes.ProbeAsync(
                    toProbe.Select(value => value.ShareCode).ToArray(), cancellationToken);
                Dictionary<string, SteamMatchProbe> byCode = results.ToDictionary(
                    value => value.Code, StringComparer.OrdinalIgnoreCase);
                foreach (SteamHistoryMatch match in toProbe)
                {
                    if (!byCode.TryGetValue(match.ShareCode, out SteamMatchProbe? probe)) continue;
                    match.PlayedAtUtc = probe.PlayedAtUtc ?? match.PlayedAtUtc;
                    match.Score = probe.Score ?? match.Score;
                    match.LastCheckedAtUtc = now;
                    match.AvailabilityErrorCode = probe.ErrorCode;
                    match.Availability = probe.ErrorCode switch
                    {
                        null when probe.DemoUrl is not null => SteamReplayAvailability.Available,
                        "DEMO_EXPIRED" => SteamReplayAvailability.Expired,
                        _ => SteamReplayAvailability.Unavailable
                    };
                }
            }
            connection.LastSyncedAtUtc = now;
            connection.LastErrorCode = null;
            connection.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return new SteamHistorySyncResult(added, toProbe.Length, capped);
        }
        catch (SteamHistoryException exception)
        {
            connection.LastErrorCode = exception.Code;
            connection.UpdatedAtUtc = now;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DisconnectAsync(string userId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        SteamHistoryConnection? connection = await db.SteamHistoryConnections
            .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);
        if (connection is null) return;
        db.SteamHistoryConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static SteamHistoryMatch CreateMatch(SteamMatchShareCode code, DateTimeOffset now) => new()
    {
        ShareCode = code.Code,
        MatchId = code.MatchId.ToString(CultureInfo.InvariantCulture),
        ReservationId = code.ReservationId.ToString(CultureInfo.InvariantCulture),
        TvPort = code.TvPort,
        Availability = SteamReplayAvailability.Unknown,
        CreatedAtUtc = now
    };
}
