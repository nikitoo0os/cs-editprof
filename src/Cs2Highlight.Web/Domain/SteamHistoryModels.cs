using System.ComponentModel.DataAnnotations;

namespace Cs2Highlight.Web.Domain;

public enum SteamReplayAvailability
{
    Unknown,
    Available,
    Expired,
    Unavailable
}

public sealed class SteamHistoryConnection
{
    public long Id { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    [MaxLength(20)] public string SteamId64 { get; set; } = string.Empty;
    [MaxLength(4096)] public string ProtectedAuthenticationCode { get; set; } = string.Empty;
    [MaxLength(64)] public string CursorShareCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    [MaxLength(64)] public string? LastErrorCode { get; set; }
    public List<SteamHistoryMatch> Matches { get; set; } = [];
}

public sealed class SteamHistoryMatch
{
    public long Id { get; set; }
    public long SteamHistoryConnectionId { get; set; }
    public SteamHistoryConnection Connection { get; set; } = null!;
    [MaxLength(64)] public string ShareCode { get; set; } = string.Empty;
    [MaxLength(32)] public string MatchId { get; set; } = string.Empty;
    [MaxLength(32)] public string ReservationId { get; set; } = string.Empty;
    public int TvPort { get; set; }
    public DateTimeOffset? PlayedAtUtc { get; set; }
    [MaxLength(32)] public string? Score { get; set; }
    public SteamReplayAvailability Availability { get; set; }
    [MaxLength(64)] public string? AvailabilityErrorCode { get; set; }
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
