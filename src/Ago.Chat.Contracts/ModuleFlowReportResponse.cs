namespace Ago.Chat.Contracts;

/// <summary>
/// `18-14`: `GET /api/v1/conversations/module-flow-report`'s response body. <see cref="From"/>/
/// <see cref="To"/> are the bound this report actually used - always present, even when the caller
/// supplied neither and the handler defaulted them, the same "the bound is visible, not silent" shape
/// `OperatorAnalyticsResponse.From`/`To` already establishes for `18-08`.
///
/// <para><b><see cref="FlowsStarted"/>/<see cref="FlowsClosed"/> are deliberately not named
/// <c>BookingsStarted</c>/<c>BookingsConfirmed</c>.</b> A closed module task is not the same fact as a
/// confirmed booking - see <c>Ago.Chat.Application.Abstractions.IModuleFlowReadStore</c>'s own remarks
/// (`ago-chat`) for the full reasoning. The console's own copy that renders these two numbers must
/// preserve the same distinction in the text a site owner actually reads (the backlog item's own
/// Done-when is explicit this is not only a code-comment concern).</para>
/// </summary>
public sealed record ModuleFlowReportResponse(
    DateTimeOffset From, DateTimeOffset To, long FlowsStarted, long FlowsClosed);
