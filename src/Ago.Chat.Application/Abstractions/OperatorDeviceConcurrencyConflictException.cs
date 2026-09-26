using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-82`: <see cref="IOperatorDeviceRepository.SaveAsync"/>'s own technology-agnostic signal that the
/// insert it just tried to commit lost a race for the same `(operatorId, installationId)` pair -
/// `ux_operator_devices_operator_installation` tripped (`OperatorDeviceConfiguration`'s own unique
/// index), not an optimistic-concurrency token: <see cref="OperatorDevice"/> carries no `xmin` check,
/// exactly like <see cref="Visitor"/> and for the same reason
/// (<see cref="Infrastructure.Postgres.VisitorRepository"/>'s own remarks), so the only way a save here
/// can fail at all is two callers each constructing their own brand-new row for the same pair and both
/// INSERTing.
///
/// <para>Declared here, next to the port it belongs to, for the identical reason
/// <see cref="VisitorConcurrencyConflictException"/> and <see cref="ConversationConcurrencyConflictException"/>
/// are: clean-architecture.md's dependency rule keeps `Ago.Chat.Application` free of any EF Core/Npgsql
/// reference, so the adapter (`Ago.Chat.Infrastructure.Postgres.OperatorDeviceRepository`) is the one
/// place in the whole call chain that knows the underlying failure is EF's `DbUpdateException` wrapping
/// Npgsql's `23505`, and it translates that into this type at the port boundary before anything reaches
/// a handler. <see cref="IOperatorCapacity"/>'s own remarks state the same rule as a prohibition: "a
/// handler must never catch <c>PostgresException</c>".</para>
///
/// <para><b>What a caller is expected to do with it.</b> Unlike
/// <see cref="VisitorConcurrencyConflictException"/>, whose one catcher has nothing to reapply
/// (`StartConversationHandler`'s own remarks), this one's caller *does*:
/// `RegisterOperatorDeviceHandler` re-reads the winner's committed row and applies its own
/// <see cref="OperatorDevice.Refresh"/> to it - the write the losing INSERT was carrying. That is what
/// makes `PUT /api/v1/me/devices/{installationId}` the idempotent upsert `adr/0179` §1 already claims it
/// is, under concurrency rather than only in sequence.</para>
/// </summary>
public sealed class OperatorDeviceConcurrencyConflictException(OperatorId operatorId, string installationId, string? deviceId = null)
    : Exception(
        $"A device row for operator {operatorId.Value}, installation '{installationId}'"
        + (deviceId is null ? string.Empty : $" and device '{deviceId}'")
        + " was created concurrently before it could be saved.")
{
    public OperatorId OperatorId { get; } = operatorId;

    public string InstallationId { get; } = installationId;

    /// <summary>`26-122`: the identity value the losing insert actually raced on, when known - see the
    /// `Ago.Chat.Infrastructure.Postgres.OperatorDeviceRepository.SaveAsync` adapter (the one place
    /// allowed to know which Postgres constraint fired) for why either identity index can be the one
    /// named.</summary>
    public string? DeviceId { get; } = deviceId;
}
