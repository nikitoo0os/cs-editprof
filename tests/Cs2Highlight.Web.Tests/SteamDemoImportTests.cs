using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Cs2Highlight.Web.Tests;

public sealed class SteamDemoImportTests
{
    [Fact]
    public void DecodesMatchShareCodeIntoGameCoordinatorFields()
    {
        SteamMatchShareCode result = SteamShareCodeDecoder.Decode(
            "CSGO-P9k3F-eVL9n-LZLXN-DrBGF-VKD7K");

        Assert.Equal(3505575050994516382UL, result.MatchId);
        Assert.Equal(3505581094013501947UL, result.ReservationId);
        Assert.Equal((ushort)12909, result.TvPort);
    }

    [Fact]
    public void RejectsMalformedOrUnknownAlphabetCode()
    {
        SteamDemoImportException exception = Assert.Throws<SteamDemoImportException>(() =>
            SteamShareCodeDecoder.Decode("CSGO-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA"));

        Assert.Equal("INVALID_MATCH_CODE", exception.Code);
    }

    [Fact]
    public void NormalizesWhitespaceAroundCode()
    {
        SteamMatchShareCode result = SteamShareCodeDecoder.Decode(
            "  CSGO-P9k3F-eVL9n-LZLXN-DrBGF-VKD7K ");

        Assert.Equal("CSGO-P9k3F-eVL9n-LZLXN-DrBGF-VKD7K", result.Code);
    }

    [Fact]
    public void RecognizesReplayThatIsFarOutsideValveRetentionWindow()
    {
        const string url =
            "http://replay271.valve.net/730/003830292815003255677_1716728332.dem.bz2";

        Assert.True(SteamReplayUrlPolicy.IsCertainlyExpired(
            url, new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void DoesNotPreemptivelyRejectRecentReplay()
    {
        long timestamp = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        string url = $"http://replay1.valve.net/730/match_{timestamp}.dem.bz2";

        Assert.False(SteamReplayUrlPolicy.IsCertainlyExpired(
            url, new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void EncryptsSteamHistoryAuthenticationCodeAtRest()
    {
        SteamHistorySecretProtector protector = new(new EphemeralDataProtectionProvider());
        const string authenticationCode = "ABCD-EFGHJ-KLMN";

        string protectedValue = protector.Protect(authenticationCode);

        Assert.DoesNotContain(authenticationCode, protectedValue, StringComparison.Ordinal);
        Assert.Equal(authenticationCode, protector.Unprotect(protectedValue));
    }

    [Fact]
    public async Task MatchHistoryApiRequiresOperatorApiKeyBeforeSendingRequest()
    {
        using SteamMatchHistoryApiClient client = new(new SteamHistoryOptions());

        SteamHistoryException exception = await Assert.ThrowsAsync<SteamHistoryException>(() =>
            client.GetNextCodeAsync(
                "76561198000000000",
                "ABCD-EFGHJ-KLMN",
                "CSGO-P9k3F-eVL9n-LZLXN-DrBGF-VKD7K",
                CancellationToken.None));

        Assert.Equal("STEAM_HISTORY_API_NOT_CONFIGURED", exception.Code);
    }
}
