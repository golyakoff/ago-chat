using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorDeviceRepository : IOperatorDeviceRepository
{
    private readonly Dictionary<OperatorDeviceId, OperatorDevice> _byId = [];

    public Task<OperatorDevice?> FindAsync(OperatorId operatorId, string installationId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(d => d.OperatorId == operatorId && d.InstallationId == installationId));

    public Task<OperatorDevice?> FindActiveByTokenAsync(PushProvider provider, string token, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(d => d.Provider == provider && d.Token == token && d.RevokedAt is null));

    public Task<IReadOnlyList<OperatorDevice>> ListActiveForOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperatorDevice>>(
            _byId.Values.Where(d => d.OperatorId == operatorId && d.RevokedAt is null).ToList());

    public Task SaveAsync(OperatorDevice device, CancellationToken cancellationToken)
    {
        _byId[device.Id] = device;
        return Task.CompletedTask;
    }

    public void Seed(OperatorDevice device) => _byId[device.Id] = device;
}
