using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `14-01`: <see cref="ExternalChannelAddress"/> to its raw <see cref="string"/> column, the same
/// shape <see cref="MessageBodyConverter"/> already uses for <c>MessageBody</c>.
///
/// <para>The read direction runs the value object's own constructor, so a row that somehow holds an
/// empty or over-long address fails loudly at materialization rather than becoming an invalid domain
/// object - the "there is no such thing as a validated-somewhere-else entity" rule
/// (clean-architecture.md) applied to the one path that bypasses the write side.</para>
/// </summary>
internal static class ExternalChannelAddressConverter
{
    public static readonly ValueConverter<ExternalChannelAddress, string> Instance = new(
        address => address.Value,
        value => new ExternalChannelAddress(value));
}
