using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterOperatorDevice;

/// <summary>`26-03`/`adr/0179` §1: backs the idempotent `PUT /api/v1/me/devices/{installationId}` -
/// calling this twice with the same <see cref="InstallationId"/> and a new <see cref="Token"/> updates
/// the one existing row, never inserts a second one (this item's own Done-when).</summary>
public sealed record RegisterOperatorDevice(
    OperatorId OperatorId, SiteId SiteId, string InstallationId, PushProvider Provider, string Platform, string Token);
