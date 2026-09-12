namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-73`: bound from <c>SiteActivityWatchdog:*</c> config keys - lives here, in Infrastructure,
/// rather than in <c>Ago.Chat.Application</c>, the same "a plain settings POCO lives beside the class
/// that reads it" choice <c>PersonExportOptions</c>'s own remarks already make: this value's two real
/// readers (<c>SiteActivityWatchdogRepository</c> and
/// <c>Ago.Chat.Infrastructure.Postgres.Pipeline.MessageBatchWriter</c>) are both Infrastructure
/// classes.
/// </summary>
public sealed class SiteActivityWatchdogOptions
{
    public const string SectionName = "SiteActivityWatchdog";

    /// <summary>
    /// The shortest gap between two recorded touches of the same site's watchdog timestamp - an
    /// implementer's-call throttle, not a measurement (`CLAUDE.md` bans inventing a "typical"
    /// production figure). <see cref="Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation"/> calls
    /// <c>ISiteActivityWatchdog.TouchAsync</c> on every authenticated request, and without this floor
    /// that would mean one <c>sites</c> row write per console page load; fifteen minutes is many
    /// multiples of any plausible request rate for one site's own operators and utterly negligible
    /// next to the three-month window the whole watchdog measures, so a fresher touch than this buys
    /// the sweep nothing it could ever act on differently. The identical column and the identical
    /// throttle also gate <see cref="Ago.Chat.Infrastructure.Postgres.Pipeline.MessageBatchWriter"/>'s
    /// own touch on an operator's outbound message - see that class's own remarks for why reusing one
    /// throttle for both call sites, rather than inventing a second number, is the right amount of
    /// symmetry here.
    /// </summary>
    public TimeSpan MinTouchInterval { get; set; } = TimeSpan.FromMinutes(15);
}
