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
/// out of one that is not part of this closed vocabulary's own documented shape. It reads a
/// <c>"prompt"</c> string for the three kinds that have one, and, `25-135`: a <c>"title"</c> string plus
/// a <c>"lines"</c> array of <c>{label, value}</c> pairs for <see cref="PrimitiveKinds.ConfirmationCard"/>
/// - "a titled summary... payload lines to read" in that kind's own remarks. That is knowledge of *the
/// four primitives Chat itself defines*, not of any module's domain - the same "shape, not meaning"
/// split <see cref="MessagePayload"/> documents for itself, applied by the one party (the primitive's
/// own owner) entitled to make it.</para>
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
    /// <param name="locale">`25-134`: the site's own configured language, as the <see cref="Locale"/>
    /// enum's PascalCase member name (<see cref="Site.Locale"/>'s own wire convention) - a plain
    /// <c>string?</c> rather than the enum itself, matching the type <see cref="Strings.For"/> already
    /// accepts and the type both call sites already resolve their own locale as. Missing or
    /// unrecognised renders in English - see <see cref="Strings.For"/>'s own remarks for why this never
    /// throws.</param>
    public static string Render(
        string fallbackBody, string kind, MessagePayload? payload, IReadOnlyList<MessageAction> actions,
        string? locale)
    {
        // `25-135`: checked ahead of IsChoiceShaped below - PrimitiveKinds.ConfirmationCard is a member
        // of that set too (it is answered by picking one of a bounded set of actions, same as any other
        // choice-shaped kind), but it carries no "prompt" field at all (title/lines instead), so without
        // this branch it fell through to the numbered-actions path with fallbackBody standing in for the
        // missing prompt - which for both of this renderer's real call sites is the visitor's own last
        // message, the trigger that produced this confirmation in the first place. A completed booking
        // therefore rendered as nothing but an echo of whatever the visitor last typed, never the actual
        // confirmation. This branch reads the card's own title/lines instead - the same "titled summary...
        // payload lines to read" shape PrimitiveKinds.ConfirmationCard's own remarks already describe -
        // and, since the shipped booking module never populates this kind's actions
        // (Ago.Calendar's own ModuleStep.ConfirmationStep passes an empty list), returns that text
        // directly rather than also trying to fold in a numbered-actions tail no real caller has ever
        // needed.
        if (kind == PrimitiveKinds.ConfirmationCard)
        {
            return TryRenderConfirmationCard(payload) ?? fallbackBody;
        }

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

            sb.Append('\n').Append(Strings.For(locale).ReplyWithTheNumber);
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

    /// <summary>
    /// `25-135`: the other two fields this vocabulary's own documented shape defines for
    /// <see cref="PrimitiveKinds.ConfirmationCard"/> specifically - a <c>"title"</c> string and a
    /// <c>"lines"</c> array of <c>{label, value}</c> string pairs, the identical two fields
    /// <c>ago-widget</c>'s own <c>render.ts</c> reads for this same wire payload
    /// (<c>ConfirmationCardContent</c>). Rendered as the title, then each line as
    /// <c>"label: value"</c> on its own line. Absent or malformed - no title at all, missing/malformed
    /// labels or values on a line - is treated the same as absent: <see langword="null"/>, so the
    /// caller falls back to <paramref name="fallbackBody"/>'s own value, never a throw, matching
    /// <see cref="TryReadPrompt"/>'s own posture.
    /// </summary>
    private static string? TryRenderConfirmationCard(MessagePayload? payload)
    {
        if (payload is not { } value)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value.Value);
            if (!document.RootElement.TryGetProperty("title", out var titleElement)
                || titleElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var sb = new StringBuilder(titleElement.GetString());
            if (document.RootElement.TryGetProperty("lines", out var linesElement)
                && linesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in linesElement.EnumerateArray())
                {
                    if (line.TryGetProperty("label", out var labelElement)
                        && labelElement.ValueKind == JsonValueKind.String
                        && line.TryGetProperty("value", out var valueElement)
                        && valueElement.ValueKind == JsonValueKind.String)
                    {
                        sb.Append('\n').Append(labelElement.GetString()).Append(": ").Append(valueElement.GetString());
                    }
                }
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// `25-134`: Chat's own two locale-aware system-message texts that live outside any module's
    /// payload - the trailing instruction line this renderer appends to every choice-shaped step, and
    /// the "module task finished with nothing further to say" fallback
    /// <see cref="RouteConversationToModule.RouteConversationToModuleHandler"/> adds when a module
    /// completes without a final step. Both are Chat's own primitive vocabulary, never a module's -
    /// the same reason this class already owns <c>"prompt"</c> - so both live here, in one small,
    /// hand-written table, rather than each caller inventing its own copy or borrowing
    /// <c>Ago.Calendar</c>'s. The shape is deliberately the identical "closed set of two" record
    /// <c>Ago.Calendar</c>'s own <c>ModuleStepFactory.Strings</c> already uses for its own locale pair -
    /// two fixed members, hand-written, no resource file or third-party i18n library, because this
    /// vocabulary has exactly two entries and pulling in translation infrastructure for two fixed
    /// strings would be the premature generalisation `clean-architecture.md` warns a product layer
    /// against.
    /// </summary>
    public sealed record Strings(string ReplyWithTheNumber, string ModuleTaskDone)
    {
        private static readonly Strings English = new(
            ReplyWithTheNumber: "Reply with the number.",
            ModuleTaskDone: "Done - thank you.");

        private static readonly Strings Russian = new(
            ReplyWithTheNumber: "Ответьте номером.",
            ModuleTaskDone: "Готово, спасибо.");

        /// <summary>Anything but a recognised <c>"Ru"</c> (case-insensitive, matching how loosely
        /// <c>Ago.Calendar</c>'s own <c>ModuleStepFactory.Strings.For</c> already treats this same
        /// hand-synchronized value) renders in English - a genuinely unconfigured value, the
        /// <see cref="Locale.En"/> default every pre-existing <see cref="Site"/> row already carries, or
        /// a locale this table has no strings for yet - never a throw, because a rendering helper must
        /// never be the reason a message fails to display.</summary>
        public static Strings For(string? locale) =>
            string.Equals(locale, nameof(Locale.Ru), StringComparison.OrdinalIgnoreCase) ? Russian : English;
    }
}
