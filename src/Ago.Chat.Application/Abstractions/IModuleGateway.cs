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

/// <summary>A site's module: its key, where to reach it, and what proves a call is really for the
/// site it claims - grouped because every resilience pipeline this boundary needs is keyed per
/// <see cref="ModuleKey"/> (`resilience.md`'s per-channel-key reasoning, reused verbatim: one module's
/// outage must not open a breaker shared with another module's calls).
///
/// <para><b>`22-02`: <see cref="SiteId"/> and <see cref="Credential"/></b>. Both ride along on every
/// call, including a reply - <see cref="SubmitModuleReplyRequest"/> carries no site id of its own (the
/// wire contract's reply route never has, since a reply is addressed by <c>externalTaskId</c> alone),
/// so this is the only place <c>Ago.Chat.Infrastructure.Modules</c> can read the site a reply is for
/// when it signs the call. <see cref="Credential"/> is the secret that signature is made with, so the
/// module on the other end can tell a genuine chat-originated call for this site from anyone who
/// reached its entry point and guessed a site id. See <see cref="Domain.ModuleCredential"/>'s own
/// remarks.</para></summary>
public sealed record EnabledModuleEndpoint(ModuleKey ModuleKey, SiteId SiteId, Uri EntryPoint, ModuleCredential Credential);

/// <summary>One step a module handed back - <c>StepDto</c> in the wire contract, translated into this
/// system's own <see cref="MessageContentKind"/>/<see cref="MessagePayload"/>/<see cref="MessageAction"/>
/// value objects at the one boundary that receives untrusted bytes, exactly as every other inbound
/// payload in this codebase is validated at its own boundary rather than trusted deeper in.</summary>
public sealed record ModuleStep(MessageContentKind Kind, MessagePayload? Payload, IReadOnlyList<MessageAction> Actions);

public sealed record StartModuleTaskRequest(Guid ChatTaskId, SiteId SiteId, ConversationId ConversationId, string TriggerText);

public sealed record StartModuleTaskResult(string ExternalTaskId, ModuleStep Step, bool Complete);

/// <param name="PhoneVerifiedAt">
/// `20-09`: the one wire call that already carries a phone number across the AGO Chat/AGO Calendar
/// boundary (the visitor's raw typed text, at whichever step a module treats as its phone field) is
/// where the verification assertion rides along, rather than a second call being invented for it
/// (`docs/backlog/20-09-*`'s own "cross-product data question", `adr/0077`'s "authenticity is checked;
/// the deeper claim is trusted" trust boundary applied here for a phone instead of a module task).
/// Null on every reply except one answering a <see cref="Domain.PrimitiveKinds.VerifiedPhoneForm"/>
/// step for which <see cref="Application.UseCases.RouteConversationToModule.RouteConversationToModuleHandler"/>
/// found an active, verified <c>ChannelIdentity</c> - see that handler's own remarks. Calendar decides
/// what a non-null value means (`20-09`'s own claim-time gate); Chat's only obligation is never to send
/// one it has not itself checked.
/// </param>
public sealed record SubmitModuleReplyRequest(
    string ExternalTaskId, Guid ChatTaskId, MessageContentKind Kind, string Value, DateTimeOffset? PhoneVerifiedAt = null);

public sealed record SubmitModuleReplyResult(ModuleStep? Step, bool Complete);
