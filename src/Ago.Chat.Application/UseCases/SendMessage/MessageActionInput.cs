namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary>
/// One action as a caller sent it - two raw strings, unvalidated, the wire's own shape.
///
/// <para>Separate from <c>Ago.Chat.Domain.MessageAction</c> on purpose, and it is the same
/// separation <see cref="SendVisitorMessage.Body"/> already has from <c>MessageBody</c>: a command
/// carries what arrived, and the handler is where "what arrived" becomes "what is valid", so that a
/// bad label is a <c>Result</c> failure and not an <c>ArgumentException</c> surfacing as a 500. A
/// command that took the domain type would push construction - and therefore the throw - out to
/// every hub method and endpoint that builds one.</para>
/// </summary>
public sealed record MessageActionInput(string Label, string Value);
