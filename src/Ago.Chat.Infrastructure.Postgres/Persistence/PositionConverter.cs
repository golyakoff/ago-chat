using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `11-01`: stores <see cref="Position"/> as the kebab-case literal `Stage11AddSiteWidgetConfig`'s own
/// `CHECK` constraint enforces (`'bottom-right'`/`'bottom-left'`) - deliberately not the default
/// `HasConversion&lt;string&gt;()` every other enum column here uses (`ConversationState`,
/// `AttachmentState`, ...), which would store the CLR member name (`"BottomRight"`) verbatim. The
/// column value is a data-model-level choice (`data-model.md`'s `sites` bullet), independent of how
/// the HTTP wire DTO stringifies the same enum (`Ago.Chat.Api`'s own PascalCase `.ToString()`,
/// `ConversationSummaryDto.State`'s own precedent) - the two boundaries are allowed to differ, the
/// same way `MessageBodyConverter` and a wire DTO's own shape can differ for `MessageBody`.
/// </summary>
internal static class PositionConverter
{
    // Ternaries, not a switch expression - ValueConverter takes an Expression<Func<...>> (EF compiles
    // it into the generated SQL/materialization code), and expression trees cannot contain a switch
    // expression or a throw expression (CS8514/CS8188), only the subset LINQ providers can translate.
    // Falling back to BottomRight/"bottom-right" for anything else is safe by construction, not just
    // convenient: Stage11AddSiteWidgetConfig's own CHECK constraint is the real guarantee that no other
    // value ever reaches the read side of this converter.
    public static readonly ValueConverter<Position, string> Instance = new(
        position => position == Position.BottomLeft ? "bottom-left" : "bottom-right",
        value => value == "bottom-left" ? Position.BottomLeft : Position.BottomRight);
}
