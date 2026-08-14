using System.ComponentModel.DataAnnotations;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

[Authorize]
[EnableRateLimiting("uploads")]
public sealed class SteamHistoryModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    UserManager<ApplicationUser> userManager,
    SteamHistoryService history,
    SteamGenerationCreationService generations) : PageModel
{
    [BindProperty] public ConnectInput Input { get; set; } = new();
    [BindProperty] public List<long> SelectedMatchIds { get; set; } = [];
    public SteamHistoryConnection? Connection { get; private set; }
    public IReadOnlyList<SteamHistoryMatch> Matches { get; private set; } = [];
    public string? ErrorCode { get; private set; }
    public string? Notice { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostConnectAsync(CancellationToken cancellationToken)
    {
        ApplicationUser user = await RequireUserAsync();
        try
        {
            await history.ConnectAsync(
                user.Id, Input.SteamId64, Input.AuthenticationCode, Input.KnownShareCode,
                cancellationToken);
            SteamHistorySyncResult result = await history.SyncAsync(user.Id, cancellationToken);
            Notice = SyncNotice(result);
        }
        catch (SteamHistoryException exception) { ErrorCode = exception.Code; }
        catch (SteamDemoImportException exception) { ErrorCode = exception.Code; }
        Input.AuthenticationCode = string.Empty;
        ModelState.Remove("Input.AuthenticationCode");
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSyncAsync(CancellationToken cancellationToken)
    {
        ApplicationUser user = await RequireUserAsync();
        try
        {
            SteamHistorySyncResult result = await history.SyncAsync(user.Id, cancellationToken);
            Notice = SyncNotice(result);
        }
        catch (SteamHistoryException exception) { ErrorCode = exception.Code; }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken cancellationToken)
    {
        ApplicationUser user = await RequireUserAsync();
        await history.DisconnectAsync(user.Id, cancellationToken);
        Notice = "История Steam отключена, сохранённый код авторизации удалён.";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        ApplicationUser user = await RequireUserAsync();
        try
        {
            long[] ids = SelectedMatchIds.Distinct().ToArray();
            if (ids.Length == 0)
                throw new SteamHistoryException("STEAM_HISTORY_NO_SELECTION", "Select at least one match.");
            await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            string[] codes = await db.SteamHistoryMatches.AsNoTracking()
                .Where(value => ids.Contains(value.Id) &&
                    value.Connection.UserId == user.Id &&
                    value.Availability == SteamReplayAvailability.Available)
                .OrderBy(value => value.PlayedAtUtc)
                .Select(value => value.ShareCode)
                .ToArrayAsync(cancellationToken);
            if (codes.Length != ids.Length)
                throw new SteamHistoryException(
                    "STEAM_HISTORY_SELECTION_INVALID", "One of the selected replays is unavailable.");
            string publicId = await generations.CreateAsync(user, codes, cancellationToken);
            return RedirectToPage("/Generation", new { publicId });
        }
        catch (SteamHistoryException exception) { ErrorCode = exception.Code; }
        catch (SteamDemoImportException exception) { ErrorCode = exception.Code; }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ErrorCode = exception.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<ApplicationUser> RequireUserAsync() =>
        await userManager.GetUserAsync(User) ??
        throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        string? userId = userManager.GetUserId(User);
        if (userId is null) return;
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Connection = await db.SteamHistoryConnections.AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);
        if (Connection is null) return;
        SteamHistoryMatch[] matches = await db.SteamHistoryMatches.AsNoTracking()
            .Where(value => value.SteamHistoryConnectionId == Connection.Id)
            .OrderByDescending(value => value.Id)
            .Take(200)
            .ToArrayAsync(cancellationToken);
        Matches = matches
            .OrderByDescending(value => value.PlayedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(value => value.Id)
            .ToArray();
    }

    private static string SyncNotice(SteamHistorySyncResult result) => result.Added switch
    {
        > 0 => $"Добавлено новых матчей: {result.Added}. Проверено replay: {result.Checked}.",
        _ => $"Новых матчей нет. Проверено replay: {result.Checked}."
    } + (result.Capped ? " Найдено много матчей — нажми «Обновить» ещё раз." : string.Empty);

    public sealed class ConnectInput
    {
        [Required, StringLength(20)] public string SteamId64 { get; set; } = string.Empty;
        [Required, StringLength(32)] public string AuthenticationCode { get; set; } = string.Empty;
        [Required, StringLength(64)] public string KnownShareCode { get; set; } = string.Empty;
    }
}
