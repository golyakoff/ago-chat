using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-73`: returns a canned <see cref="OperatorInviteProvisionOutcome"/> regardless of the
/// request - this port's own real Keycloak Admin API behaviour (create-or-find-by-email, the
/// `execute-actions-email` call, locale) only means anything against a real Keycloak, so
/// <c>CreateOperatorInviteHandlerTests</c> uses this fake purely to prove the handler's own decisions
/// (validate, rate-limit, generate, then call this port and record a send failure if told to), the same
/// split <c>FakeOperatorInviteRedemptionRepository</c>'s own remarks describe for
/// <c>OperatorInviteRedemptionRepository</c>.</summary>
public sealed class FakeOperatorInviteEmailProvisioner(OperatorInviteProvisionOutcome? outcome = null) : IOperatorInviteEmailProvisioner
{
    public OperatorInviteProvisionRequest? LastRequest { get; private set; }

    public Task<OperatorInviteProvisionOutcome> ProvisionAndSendAsync(
        OperatorInviteProvisionRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(outcome ?? new OperatorInviteProvisionOutcome.Sent());
    }
}
