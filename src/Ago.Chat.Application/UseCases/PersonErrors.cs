using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>`adr/0184`: the Person read API's own refusals. A person id that exists but belongs to
/// another account reads as <see cref="NotFound"/>, never a different, more informative error - the
/// same "wrong tenant reads like no such row" shape <see cref="ConversationErrors.NotFound"/>'s own
/// callers already use.</summary>
public static class PersonErrors
{
    public static Error NotFound(Guid personId) =>
        new("Person.NotFound", $"Person {personId} was not found.");

    /// <summary>The batch read caps how many ids one request may name - a console page fetches the
    /// people on the screen it is drawing, never the whole account.</summary>
    public static Error TooManyIds(int max) =>
        new("Person.TooManyIds", $"At most {max} person ids may be requested at once.");
}
