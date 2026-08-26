using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Api.Cors;

/// <summary>
/// `5-18`: `HubOriginValidator`'s counterpart for the operator side - the same question ("may this
/// browser open this hub connection") asked against the right list.
///
/// <para>A separate type rather than a second method on <see cref="HubOriginValidator"/>, because the
/// two answer to different owners: that one reads a *tenant's* row from the database and is therefore
/// per-connection and asynchronous; this one reads one deployment-wide setting and is neither. Keeping
/// them apart is also what stops the next person wiring the operator hub back to the tenant list by
/// picking the nearer-looking method - which is the mistake `5-18` exists to undo.</para>
/// </summary>
public sealed class ConsoleOriginValidator(ConsoleOriginOptions options)
{
    public bool IsAllowed(HubCallerContext context)
    {
        var origin = context.GetHttpContext()?.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Same reasoning HubOriginValidator gives for the identical branch: no cross-origin claim
            // to verify. A same-origin caller (the dev harness) or a non-browser client sends none,
            // and a browser cannot omit it.
            return true;
        }

        return options.AllowedOrigins.Contains(origin, StringComparer.Ordinal);
    }
}
