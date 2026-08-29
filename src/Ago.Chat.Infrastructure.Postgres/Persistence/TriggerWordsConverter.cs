using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`20-07`: <see cref="Domain.EnabledModule.TriggerWords"/> as a JSON array in one <c>text</c>
/// column - the identical "small, bounded list, never queried into by Postgres" reasoning
/// <c>MessageContentConverters.Actions</c> gives for a message's own actions list.</summary>
internal static class TriggerWordsConverter
{
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<IReadOnlyList<string>, string> Instance = new(
        words => JsonSerializer.Serialize(words, StorageOptions),
        value => JsonSerializer.Deserialize<List<string>>(value, StorageOptions)!);

    /// <summary>EF needs one for any collection behind a value converter - the same reason
    /// <c>MessageContentConverters.ActionsComparer</c> exists.</summary>
    public static readonly ValueComparer<IReadOnlyList<string>> Comparer = new(
        (left, right) => left == null ? right == null : right != null && left.SequenceEqual(right),
        words => words == null ? 0 : words.Aggregate(0, (hash, word) => HashCode.Combine(hash, word)),
        words => words == null ? words! : words.ToList());
}
