using Microsoft.AspNetCore.SignalR;

namespace Cs2Highlight.Web.Hubs;

public sealed class GenerationHub : Hub
{
    public Task Subscribe(string publicId)
    {
        if (publicId.Length is < 20 or > 64 || publicId.Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new HubException("Invalid generation ID.");
        return Groups.AddToGroupAsync(Context.ConnectionId, publicId);
    }
}
