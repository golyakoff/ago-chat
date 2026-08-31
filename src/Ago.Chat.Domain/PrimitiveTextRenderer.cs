using System.Text;
using System.Text.Json;

namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`: the canonical plain-text rendering of a module step - "a primitive without one is not
/// finished" (backlog item's own Scope). Pure, no I/O, Domain-owned for the identical reason
/// <see cref="TriggerCommandMatcher"/>/<see cref="ChoiceReplyTextResolver"/> are: nothing here reads
/// anything but its own arguments.
///
/// <para><b>Reads <see cref="MessageContentKind"/> and <see cref="MessagePayload"/>, and that looks
/// like the opacity rule being broken - it is not.</b> `MessagePayload`'s own remarks say AGO Chat
/// "owns no schema" for a payload's *fields*; this renderer never reads a field name or a field value
/// out of one. It reads only the two facts every payload in this closed vocabulary is documented to
/// carry regardless of which module produced it - a <c>"prompt"</c> string for the three kinds that
/// have one, and nothing at all for <see cref="PrimitiveKinds.ConfirmationCard"/>, whose own title/lines
/// shape this renderer does not walk. That is knowledge of *the four primitives Chat itself defines*,
/// not of any module's domain - the same "shape, not meaning" split <see cref="MessagePayload"/>
/// documents for itself, applied by the one party (the primitive's own owner) entitled to make it.</para>
///
/// <para><b>Falls back to <see cref="Message.Body"/> for an unrecognised kind, deliberately.</b> A
/// module wired against a future fifth primitive this build has never heard of should degrade to plain
/// prose, not throw - matching the same "an old client renders the body and numbers the actions"
/// forward-compatibility <see cref="MessageContent"/>'s own remarks describe for a channel with no UI.</para>
/// </summary>
public static class PrimitiveTextRenderer
{
    /// <param name="fallbackBody">The message's own <see cref="Message.Body"/> - always rendered
    /// first, matching the rendering contract's "Body is the fallback, and it is mandatory" rule. A
    /// text channel that already prints the body verbatim can skip calling this at all for a message
    /// whose kind it does not recognise; this function exists for the channels that want one consistent
    /// rendering regardless of kind.</param>
    public static string Render(
        string fallbackBody, string kind, MessagePayload? payload, IReadOnlyList<MessageAction> actions)
    {
        var prompt = TryReadPrompt(payload) ?? fallbackBody;

        if (PrimitiveKinds.IsChoiceShaped(kind))
        {
            if (actions.Count == 0)
            {
                return prompt;
            }

            var sb = new StringBuilder(prompt);
            sb.Append('\n');
            for (var i = 0; i < actions.Count; i++)
            {
                sb.Append(i + 1).Append(") ").Append(actions[i].Label);
                if (i < actions.Count - 1)
                {
                    sb.Append('\n');
                }
            }

            sb.Append("\nReply with the number.");
            return sb.ToString();
        }

        if (kind == PrimitiveKinds.Form || kind == PrimitiveKinds.Escalate || kind == PrimitiveKinds.VerifiedPhoneForm)
        {
            // `19-03`: an escalate step asks nothing, so it renders exactly like `form`'s own prompt-only
            // case - the difference between the two kinds is what the routing handler does with the task
            // afterward (force-closed, `RouteConversationToModuleOutcome.Escalated`), never how this
            // function renders it. `fallbackBody` is the caller's own concern too: for escalate specifically,
            // callers should not pass the visitor's own last message as the fallback (see
            // RouteConversationToModuleHandler's own remarks on why that default would be wrong here).
            // `20-09`: a verified-phone-form step's wire payload is shaped exactly like a plain form's
            // (prompt, field id, field label) - the only thing that differs is what the routing handler
            // does with the reply, never how this function renders the prompt.
            return prompt;
        }

        // An unrecognised kind: the body alone, per this type's own remarks.
        return fallbackBody;
    }

    /// <summary>
    /// The one field this renderer reads out of a payload - present, by this vocabulary's own
    /// documented shape, on every kind except <see cref="PrimitiveKinds.ConfirmationCard"/>. Absent or
    /// malformed is treated the same as absent: <see langword="null"/>, never a throw, because a
    /// rendering helper must never be the reason a message fails to display.
    /// </summary>
    private static string? TryReadPrompt(MessagePayload? payload)
    {
        if (payload is not { } value)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value.Value);
            return document.RootElement.TryGetProperty("prompt", out var promptElement)
                && promptElement.ValueKind == JsonValueKind.String
                ? promptElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
