using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Stands in for `Ago.Platform.Storage.S3.S3FileStorage` - no MinIO needed for an
/// Application-level test. <see cref="SetMetadata"/> is how a test simulates "what a HEAD-verify
/// actually found on the object", independent of what <see cref="CreateUploadAsync"/> was told to
/// presign - exactly the gap `ConfirmAttachmentHandler`'s mismatch tests exercise.</summary>
public sealed class FakeFileStorage : IFileStorage
{
    private readonly Dictionary<string, ObjectMetadata> _metadata = [];

    public int CreateUploadCalls { get; private set; }

    public int CreateDownloadUrlCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    /// <summary>`5-08`: lets a test simulate S3/MinIO being unreachable at delete time, the one
    /// failure `DeleteAttachmentHandler`/`AttachmentOrphanSweepJob` both catch and log rather than
    /// let fail the whole operation (`FileStorageUnavailableException`'s own doc comment,
    /// `Ago.Platform.Abstractions`).</summary>
    public bool ThrowUnavailableOnDelete { get; set; }

    public void SetMetadata(ObjectKey key, ObjectMetadata? metadata)
    {
        if (metadata is null)
        {
            _metadata.Remove(key.Value);
        }
        else
        {
            _metadata[key.Value] = metadata;
        }
    }

    public Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken)
    {
        CreateUploadCalls++;
        return Task.FromResult(new PresignedUpload(new Uri($"https://fake-storage.test/{key.Value}"), DateTimeOffset.UtcNow.Add(constraints.Lifetime)));
    }

    public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        CreateDownloadUrlCalls++;
        return Task.FromResult(new Uri($"https://fake-storage.test/{key.Value}?download"));
    }

    public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        Task.FromResult(_metadata.GetValueOrDefault(key.Value));

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        DeleteCalls++;
        if (ThrowUnavailableOnDelete)
        {
            throw new FileStorageUnavailableException($"Fake storage unavailable while deleting {key.Value}.");
        }

        _metadata.Remove(key.Value);
        return Task.CompletedTask;
    }
}
