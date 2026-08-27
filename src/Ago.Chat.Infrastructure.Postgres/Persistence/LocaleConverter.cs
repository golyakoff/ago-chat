using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `11-10`: stores <see cref="Locale"/> as the lowercase literal `Stage11AddSiteWidgetLocale`'s own
/// `CHECK` constraint enforces (`'en'`/`'ru'`) - mirroring <see cref="PositionConverter"/> exactly,
/// including its reason for existing at all: deliberately not the default `HasConversion&lt;string&gt;()`
/// every other enum column here uses, which would store the CLR member name (`"En"`) verbatim. The
/// column value is a data-model-level choice, independent of how the HTTP wire DTO stringifies the
/// same enum (`Ago.Chat.Api`'s own PascalCase `.ToString()`, matching `WidgetConfigEndpoints`'s own
/// `Position` convention) - the two boundaries are allowed to differ, the same way
/// `PositionConverter`'s own remarks describe for its enum.
/// </summary>
internal static class LocaleConverter
{
    // Ternaries, not a switch expression - same reason PositionConverter gives: ValueConverter takes
    // an Expression<Func<...>>, and expression trees cannot contain a switch expression or a throw
    // expression (CS8514/CS8188). Falling back to Locale.En/"en" for anything else is safe by
    // construction, not just convenient: Stage11AddSiteWidgetLocale's own CHECK constraint is the real
    // guarantee that no other value ever reaches the read side of this converter.
    public static readonly ValueConverter<Locale, string> Instance = new(
        locale => locale == Locale.Ru ? "ru" : "en",
        value => value == "ru" ? Locale.Ru : Locale.En);
}
