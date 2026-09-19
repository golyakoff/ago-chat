using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records what was pushed, without any real HTTP call - `25-160`'s own
/// <c>SubmitLogoUploadHandler</c> is the one Application-layer caller of this port.</summary>
public sealed class FakePresignedUrlUploader : IPresignedUrlUploader
{
    public int CallCount { get; private set; }

    public Task PutAsync(Uri url, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}
