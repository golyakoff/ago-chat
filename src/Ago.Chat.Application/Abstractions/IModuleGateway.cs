using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`/`adr/0065`'s own "to be decided after `20-06`" transport question, decided by this item's
/// implementation: the wire, over in-process - `adr/0065`'s own "leaning" section already named the
/// reasoning (most steps run at human pace; an unreachable module degrades honestly into the
/// unsuppressible escape to an operator the contract already requires). The ADR that formally records
/// this decision together with a measured step latency is Done-when's own separate item and had not
/// been written as of this change - see this repository's own report for the honest status. The
/// Application-facing shape of the two
/// calls the wire contract defines - <c>POST .../module-tasks</c> and
/// <c>POST .../module-tasks/{externalTaskId}/replies</c> - kept as thin as <see
/// cref="Abstractions.IInboundChannelAdapter"/> is kept for the same reason: an implementation is
/// written as if the module always answers, and resilience is applied by wrapping it
/// (<c>Ago.Chat.Infrastructure.Modules.ResilientModuleGateway</c> over <c>Ago.Platform.Resilience</c>),
/// never inside this interface.
///
/// <para><b>Both methods throw <see cref="ModuleUnreachableException"/> on any failure - never a
/// <c>Result</c>.</b> Unlike <see cref="IInboundChannelAdapter.SendAsync"/>, the wire contract defines
/// no "expected" business refusal a module can hand back on this boundary (a channel provider can
/// terminally refuse one recipient; a module answering "no" to a task it was asked to start is not a
/// concept the contract has). Every non-200, timeout or connection failure is treated identically - "the
/// module is unreachable" - which is exactly what the backlog item's own escalation rule needs to react
/// to uniformly, regardless of which of those three actually happened.</para>
/// </summary>
public interface IModuleGateway
{
    Task<StartModuleTaskResult> StartTaskAsync(
        EnabledModuleEndpoint module, StartModuleTaskRequest request, CancellationToken cancellationToken);

    Task<SubmitModuleReplyResult> SubmitReplyAsync(
        EnabledModuleEndpoint module, SubmitModuleReplyRequest request, CancellationToken cancellationToken);
}

/// <summary>A module's key and where to reach it - grouped because every resilience pipeline this
/// boundary needs is keyed per <see cref="ModuleKey"/> (`resilience.md`'s per-channel-key reasoning,
/// reused verbatim: one module's outage must not open a breaker shared with another module's calls).</summary>
public sealed record EnabledModuleEndpoint(ModuleKey ModuleKey, Uri EntryPoint);

/// <summary>One step a module handed back - <c>StepDto</c> in the wire contract, translated into this
/// system's own <see cref="MessageContentKind"/>/<see cref="MessagePayload"/>/<see cref="MessageAction"/>
/// value objects at the one boundary that receives untrusted bytes, exactly as every other inbound
/// payload in this codebase is validated at its own boundary rather than trusted deeper in.</summary>
public sealed record ModuleStep(MessageContentKind Kind, MessagePayload? Payload, IReadOnlyList<MessageAction> Actions);

public sealed record StartModuleTaskRequest(Guid ChatTaskId, SiteId SiteId, ConversationId ConversationId, string TriggerText);

public sealed record StartModuleTaskResult(string ExternalTaskId, ModuleStep Step, bool Complete);

public sealed record SubmitModuleReplyRequest(string ExternalTaskId, Guid ChatTaskId, MessageContentKind Kind, string Value);

public sealed record SubmitModuleReplyResult(ModuleStep? Step, bool Complete);
