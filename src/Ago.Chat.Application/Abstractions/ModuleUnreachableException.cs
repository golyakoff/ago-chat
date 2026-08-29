using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`: <see cref="IModuleGateway"/>'s own technology-agnostic signal that a module could not be
/// reached - any non-200 response, a timeout, a connection failure, or a call rejected by an open
/// circuit breaker. Declared here, next to the port it belongs to, the identical shape
/// <see cref="ConversationConcurrencyConflictException"/>'s own remarks describe: the Infrastructure
/// adapter (<c>Ago.Chat.Infrastructure.Modules.HttpModuleGateway</c>/<c>ResilientModuleGateway</c>) is
/// the one place that knows the underlying failure was an <c>HttpRequestException</c>, a
/// <c>TaskCanceledException</c> or Polly's own <c>BrokenCircuitException</c>, and it translates all
/// three into this one type before it ever reaches <c>RouteConversationToModuleHandler</c> - which is
/// exactly the backlog item's own escalation rule: "any failure calling the module... must" degrade
/// the same way, regardless of which of those it was.
/// </summary>
public sealed class ModuleUnreachableException(ModuleKey moduleKey, string reason, Exception? innerException = null)
    : Exception($"Module '{moduleKey}' is unreachable: {reason}", innerException)
{
    public ModuleKey ModuleKey { get; } = moduleKey;
}
