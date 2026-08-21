using StackExchange.Redis;
using Testcontainers.Redis;

namespace Ago.Chat.Integration.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer _container = null!;

    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new RedisBuilder("redis:7-alpine").Build();
        await _container.StartAsync();
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Multiplexer.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
