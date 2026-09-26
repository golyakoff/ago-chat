using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterOperatorDevice;

/// <summary>`26-03`/`adr/0179` §1: backs the idempotent `PUT /api/v1/me/devices/{installationId}` -
/// calling this twice with the same <see cref="InstallationId"/> and a new <see cref="Token"/> updates
/// the one existing row, never inserts a second one (this item's own Done-when).
///
/// <para>`26-122`: <see cref="DeviceId"/> is the row's real identity now (`OperatorDevice`'s own
/// remarks) - nullable only so a caller that has not been updated to send one still degrades to the
/// pre-`26-122` installation-keyed behaviour rather than failing the request; the shipped Android
/// client (this item's own other half) always supplies one.</para></summary>
public sealed record RegisterOperatorDevice(
    OperatorId OperatorId, SiteId SiteId, string InstallationId, PushProvider Provider, string Platform, string Token,
    string? DeviceId = null);
