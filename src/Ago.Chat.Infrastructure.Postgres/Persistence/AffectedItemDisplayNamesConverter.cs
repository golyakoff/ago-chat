using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`23-88`: <see cref="Domain.ModuleQuantityImpactPreview.AffectedItemDisplayNames"/> as a
/// JSON array in one <c>text</c> column - the identical "small, bounded list, never queried into by
/// Postgres" shape <see cref="TriggerWordsConverter"/> already establishes for its own sibling
/// opaque-string list, copied rather than shared because the two lists mean genuinely different
/// things (trigger words a chat operator typed versus opaque names a module handed back) and nothing
/// is gained by making one a special case of the other.</summary>
internal static class AffectedItemDisplayNamesConverter
{
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<IReadOnlyList<string>, string> Instance = new(
        names => JsonSerializer.Serialize(names, StorageOptions),
        value => JsonSerializer.Deserialize<List<string>>(value, StorageOptions)!);

    public static readonly ValueComparer<IReadOnlyList<string>> Comparer = new(
        (left, right) => left == null ? right == null : right != null && left.SequenceEqual(right),
        names => names == null ? 0 : names.Aggregate(0, (hash, name) => HashCode.Combine(hash, name)),
        names => names == null ? names! : names.ToList());
}
