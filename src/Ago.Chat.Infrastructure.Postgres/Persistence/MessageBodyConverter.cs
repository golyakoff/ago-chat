using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal static class MessageBodyConverter
{
    public static readonly ValueConverter<MessageBody, string> Instance =
        new(body => body.Value, value => new MessageBody(value));
}
