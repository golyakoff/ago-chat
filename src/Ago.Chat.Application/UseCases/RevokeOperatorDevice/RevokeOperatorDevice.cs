using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeOperatorDevice;

/// <summary>`26-03`/`adr/0179` §1: backs the sign-out `DELETE /api/v1/me/devices/{installationId}` -
/// scoped to the caller's own <see cref="OperatorId"/>, so a caller can never revoke a device belonging
/// to another operator by guessing its installation id.</summary>
public sealed record RevokeOperatorDevice(OperatorId OperatorId, string InstallationId);
