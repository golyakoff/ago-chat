namespace Ago.Chat.Contracts;

/// <summary>
/// One choice on a structured message, on the wire.
///
/// <para>Two strings, and no presentation hint of any kind - no "primary", no icon, no style. A hint
/// would be AGO Chat having an opinion about how another product's choice should look, and it would
/// be an opinion a text-only channel could not honour anyway. What a renderer gets is a label and an
/// order, which is exactly enough to draw a button or to print <c>"1) Label"</c>.</para>
///
/// <para><see cref="Value"/> is opaque and travels back verbatim inside the producer's own payload
/// when the customer answers - see <c>Ago.Chat.Domain.MessageAction</c> for why there is no separate
/// action endpoint.</para>
/// </summary>
public sealed record MessageActionDto(string Label, string Value);
