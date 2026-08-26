using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary>
/// Turns the raw strings a caller sent into a validated <see cref="MessageContent"/>, or into an
/// ordinary <c>Result</c> failure.
///
/// <para>Shared by both send handlers because both do exactly this and nothing more - and kept as a
/// static binder rather than a constructor overload on <see cref="MessageContent"/> so that the
/// <c>ArgumentException</c>-to-<c>Error</c> translation lives in Application, where errors are a
/// concept, instead of in Domain, where they are not.</para>
///
/// <para><b>Every check here is about shape.</b> Well-formed JSON, an object rather than a scalar,
/// lengths, a bounded number of actions, no two actions sharing a value. None of them looks at a
/// field name, and none of them may ever start to - <c>MessageOpacityTests</c> is what notices if
/// one does.</para>
/// </summary>
internal static class StructuredContentBinder
{
    /// <summary>
    /// <see langword="null"/> content for a caller that sent none, which is every caller written
    /// before `14-06` and most callers after it.
    ///
    /// <para>A payload or actions without a kind is refused rather than defaulted: the kind is the
    /// only thing that tells a renderer which renderer to use, so content without one is content
    /// nobody can draw. Defaulting it would mean AGO Chat inventing a vocabulary word, which is the
    /// one thing this design will not do.</para>
    /// </summary>
    public static Result<MessageContent?> Bind(
        string? contentKind, string? payload, IReadOnlyList<MessageActionInput>? actions)
    {
        var hasKind = !string.IsNullOrWhiteSpace(contentKind);
        var hasPayload = !string.IsNullOrWhiteSpace(payload);
        var hasActions = actions is { Count: > 0 };

        if (!hasKind)
        {
            return hasPayload || hasActions
                ? ConversationErrors.InvalidContent(
                    "A structured payload or a set of actions needs a content kind; without one, nothing can "
                    + "decide how to render it.")
                : Result<MessageContent?>.Success(null);
        }

        try
        {
            var built = MessageContent.Create(
                new MessageContentKind(contentKind!),
                hasPayload ? new MessagePayload(payload!) : null,
                actions?.Select(action => new MessageAction(action.Label, action.Value)).ToList());

            return Result<MessageContent?>.Success(built);
        }
        catch (ArgumentException exception)
        {
            // The one place a domain constructor's throw becomes a caller-facing rejection. Every
            // one of these is something the caller can fix by sending different bytes, which is what
            // makes it a 400-shaped failure rather than a fault (coding-style.md: exceptions are for
            // the unexpected).
            return ConversationErrors.InvalidContent(exception.Message);
        }
    }
}
