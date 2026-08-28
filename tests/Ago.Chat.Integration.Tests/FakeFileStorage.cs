using Ago.Platform.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>Stands in for a real <c>S3FileStorage</c> in a stripped-down test host that maps
/// <c>SitesEndpoints</c> but has no MinIO fixture of its own (`FakeRateLimiter`'s own precedent) - the
/// export routes' handlers need <see cref="IFileStorage"/> resolvable for DI, even in tests that never
/// actually call those routes: ASP.NET Core's endpoint metadata is built eagerly for every mapped
/// route the first time any request is authorized (`AuthorizationPolicyCache`'s own constructor
/// enumerates the whole <c>EndpointDataSource</c>), so a handler's constructor dependency has to
/// resolve even for an endpoint the test itself never exercises.</summary>
public sealed class FakeFileStorage : IFileStorage
{
    public Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken) =>
        Task.FromResult(new PresignedUpload(new Uri($"https://fake-storage.test/{key.Value}"), DateTimeOffset.UtcNow.Add(constraints.Lifetime)));

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://fake-storage.test/{key.Value}?download"));

    public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        Task.FromResult<ObjectMetadata?>(null);

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => Task.CompletedTask;
}
