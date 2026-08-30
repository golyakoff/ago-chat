using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetModuleFlowReportForSite;

/// <summary>
/// `18-14`: the console's own chat-to-booking conversion report - how many conversations started the
/// configured module's flow, and how many of those flows closed, for one site over one window. See
/// <see cref="IModuleFlowReadStore"/>'s own remarks for what "started"/"closed" honestly mean and do
/// not mean; every decision about the query's own shape lives there, not here, the same "the port owns
/// the definitions, the handler only shapes the wire response" split `18-08`'s
/// <c>GetOperatorAnalyticsForSiteHandler</c> already establishes.
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/>, the same call `18-08`'s own handler makes
/// for the identical reason</b> - this report is computed over every conversation on the site, not the
/// caller's own assigned/waiting ones, so it belongs to the site-wide oversight boundary
/// `authorization.md`'s admin/supervisor role draws, not to <see cref="Permission.ConversationRead"/>
/// (which every ordinary operator already holds).</para>
///
/// <para><b>The module key is resolved here, once, from <see cref="ModuleFlowReportOptions"/> - never
/// from a literal.</b> <see cref="Domain.ModuleKey"/>'s own constructor is the validation
/// (charset/length); a malformed config value throws here rather than at startup only because
/// <c>ChatModule</c>'s own <c>.Validate()</c> predicate already runs the identical construction during
/// options binding (`ModuleFlowReportOptions`'s own remarks) - reaching this line with an invalid
/// value would mean that startup check was bypassed, which only happens in a unit test that constructs
/// this handler directly with a deliberately bad option, not in a running host.</para>
/// </summary>
public sealed class GetModuleFlowReportForSiteHandler(
    IModuleFlowReadStore readStore, IPermissionChecker permissions, IClock clock, ModuleFlowReportOptions options)
{
    /// <summary>Thirty days, the same width `18-08`'s own
    /// <c>GetOperatorAnalyticsForSiteHandler.DefaultWindowDays</c> uses for an analogous "no range
    /// named" default - restated here rather than referenced (`Ago.Chat.Application` has no
    /// cross-use-case constant for it, the same precedent that handler's own remarks name), and, like
    /// that one, an operational default rather than a measurement.</summary>
    public const int DefaultWindowDays = 30;

    public async Task<Result<ModuleFlowReportResponse>> HandleAsync(
        GetModuleFlowReportForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's module flow report.");
        }

        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-DefaultWindowDays);
        if (from >= to)
        {
            return ConversationErrors.ModuleFlowInvalidRange("The report range's start must be before its end.");
        }

        var moduleKey = new ModuleKey(options.ModuleKey);
        var result = await readStore.GetSiteModuleFlowReportAsync(query.SiteId, moduleKey, from, to, cancellationToken);

        return new ModuleFlowReportResponse(from, to, result.FlowsStarted, result.FlowsClosed);
    }
}
