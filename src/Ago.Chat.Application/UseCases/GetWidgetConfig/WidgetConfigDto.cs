using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetWidgetConfig;

/// <summary>
/// What `GetWidgetConfigHandler`/`UpdateWidgetConfigHandler` (`UseCases.UpdateWidgetConfig`, same
/// assembly) return - `Position` stays the typed Domain enum here, not a string; `Ago.Chat.Api`'s own
/// endpoint file is where that gets stringified for the wire (api-design.md's error/DTO boundary
/// convention: Application deals in typed values, only the HTTP edge serializes them), the same split
/// `ConversationSummaryDto.State` draws one layer further out.
/// </summary>
public sealed record WidgetConfigDto(string? PrimaryColorHex, Position Position);
