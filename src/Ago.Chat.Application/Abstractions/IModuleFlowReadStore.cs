using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-14`: the read-side port behind the console's own "chat-to-booking conversion" block - hand-
/// written SQL over the write model, never through an aggregate (`adr/0004`), the same mechanism every
/// other read model in this codebase uses, and the same reason `18-08` got its own
/// <see cref="IOperatorAnalyticsReadStore"/> instead of a method on <see cref="IConversationReadStore"/>:
/// this answers "compute an aggregate over `module_tasks`", a genuinely different table and question
/// from either of those two ports.
///
/// <para><b>This is not a confirmed-booking count, and the name says so on purpose.</b> `20-07`/
/// `adr/0065` decision 1: Chat holds a <see cref="Domain.ModuleTask"/>'s id and whether it is
/// <see cref="ModuleTaskState.Open"/> or <see cref="ModuleTaskState.Closed"/>, nothing about what the
/// module did inside it. `adr/0077` confirmed there is no cross-repo query into AGO Calendar's own
/// Postgres to learn more. A visitor abandoning the flow, an operator closing the conversation
/// mid-step, and a flow that finished with every slot declined all close the task identically to a
/// real confirmed booking - closing is the only signal this schema can hold today. So
/// <see cref="ModuleFlowReportResult.FlowsStarted"/>/<see cref="ModuleFlowReportResult.FlowsClosed"/>
/// mean exactly "a task was opened against this module" / "that task later closed" - never "booked",
/// "converted" or "confirmed". Every caller of this port, and every string the console renders from
/// its answer, must preserve that distinction (the backlog item's own Scope section is explicit this is
/// not a wording nitpick).</para>
///
/// <para><b>The module key is caller-supplied, never a literal in this file or any other file under
/// `Ago.Chat.*`'s `src/`.</b> `adr/0065` guard 9's third check (`Ago.Chat.Architecture.Tests.
/// ModuleKeyLiteralGuardTests`) fails the build on a source-code literal of a known module key -
/// `if (moduleKey == "calendar")` compiles to a string, not a type reference, and is exactly the
/// shortcut that guard exists to catch. <c>Application.UseCases.GetModuleFlowReportForSite.
/// GetModuleFlowReportForSite</c> carries the <see cref="ModuleKey"/> the caller means, constructed by
/// <c>Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSiteHandler</c> from
/// <c>ModuleFlowReportOptions</c> (bound from configuration - JSON, not C# source, so it is outside
/// what the Roslyn scan reads) rather than from a literal anywhere in this assembly.</para>
///
/// <para><b>Tasks, not conversations, is this report's denominator.</b>
/// <see cref="Domain.Conversation.StartModuleTask"/> only rejects a *second concurrent* active task
/// (`InvalidConversationStateException` when <see cref="Domain.Conversation.ActiveModuleTask"/> is
/// already set) - it does not reject starting a new task once an earlier one on the same conversation
/// has closed. A conversation can therefore hold more than one <c>module_tasks</c> row for the same
/// module key over its lifetime (abandon a booking flow, later start a second one). This port counts
/// <em>tasks</em>, matching "how many booking flows were started" rather than "how many conversations
/// touched booking" - the two stop being the same number the moment a conversation restarts the flow,
/// and a task-level count is the one that stays accurate under that case without silently collapsing
/// two real, distinct flow attempts into one. See the backlog item's own "Open questions" section,
/// resolved this way and recorded here rather than left implicit.</para>
///
/// <para><b>Cross-site isolation</b> is an ordinary <c>WHERE conversations.site_id = @SiteId</c>
/// predicate reached through a join from <c>module_tasks</c> (which carries no <c>site_id</c> column of
/// its own - only <c>conversation_id</c>), the same shape every other site-scoped read in this codebase
/// already uses; this is not `12-02`'s cross-tenant exception.</para>
///
/// <para><b>Not a caching concern</b> (`CLAUDE.md` rule 8, `caching.md`) - the identical reasoning
/// <see cref="IOperatorAnalyticsReadStore"/>'s own remarks give: pure observability for a human reading
/// a report, at human frequency, feeding no write or compare-and-set anywhere.</para>
/// </summary>
public interface IModuleFlowReadStore
{
    /// <summary>
    /// <paramref name="from"/> is inclusive, <paramref name="to"/> is exclusive - the same half-open
    /// convention <see cref="IOperatorAnalyticsReadStore.GetSiteAnalyticsAsync"/> documents. A task is
    /// selected by its own <c>opened_at</c> falling in that range - the moment the booking flow
    /// actually started, mirroring how `18-08` selects conversations by <c>created_at</c> rather than
    /// by any later message's timestamp.
    /// </summary>
    Task<ModuleFlowReportResult> GetSiteModuleFlowReportAsync(
        SiteId siteId, ModuleKey moduleKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>
/// `18-14`: the answer to "how many conversations started this module's flow, and how many of those
/// flows closed" for one site, one module key, over one caller-supplied window. See
/// <see cref="IModuleFlowReadStore"/>'s own remarks for exactly what "started"/"closed" do and do not
/// claim - this is deliberately not called <c>BookingsStarted</c>/<c>BookingsConfirmed</c>.
/// </summary>
/// <param name="FlowsStarted">`module_tasks` rows for this site and module key, opened inside the
/// window - every task counts once, regardless of its current state.</param>
/// <param name="FlowsClosed">The subset of <paramref name="FlowsStarted"/> whose
/// <see cref="ModuleTaskState"/> is <see cref="ModuleTaskState.Closed"/> at query time. A task still
/// <see cref="ModuleTaskState.Open"/> when this report runs is counted in
/// <paramref name="FlowsStarted"/> only, the same "not yet resolved either way" treatment
/// <see cref="OperatorAnalyticsBucket.MissedCount"/>'s own remarks give a conversation still
/// <c>Waiting</c>/<c>Assigned</c>.</param>
public sealed record ModuleFlowReportResult(long FlowsStarted, long FlowsClosed);
