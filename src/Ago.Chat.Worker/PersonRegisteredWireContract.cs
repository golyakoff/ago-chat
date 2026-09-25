namespace Ago.Chat.Worker;

/// <summary>
/// `adr/0184` decision 2: this product's own copy of the wire shape a module publishes when it minted a
/// person id for a booking with no chat origin - today <c>Ago.Calendar.Contracts.PersonRegistered</c>,
/// independently declared here, never shared, the identical reasoning every other module-side wire
/// contract in this project (<see cref="ModuleQuantityImpactComputedWireContract"/>) gives for itself.
///
/// <para>Property names match the source record exactly - the publisher serialises with
/// <see cref="System.Text.Json.JsonSerializer"/>'s default options (no camelCase policy), so the wire
/// carries PascalCase field names and this record's own properties are named to match. <c>AccountId</c> is
/// the module's word for what this product calls a site (`adr/0093`: the same value).</para>
/// </summary>
internal sealed record PersonRegisteredWireContract(
    Guid PersonId,
    Guid AccountId,
    string Phone,
    string? Name,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);
