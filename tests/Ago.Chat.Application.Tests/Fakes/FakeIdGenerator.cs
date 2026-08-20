using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeIdGenerator : IIdGenerator
{
    public Guid NewId(DateTimeOffset now) => Guid.NewGuid();
}
