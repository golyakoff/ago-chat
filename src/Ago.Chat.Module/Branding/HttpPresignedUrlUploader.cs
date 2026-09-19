using System.Net.Http.Headers;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.Branding;

/// <summary>`25-160`: the one Infrastructure-side implementation of
/// <see cref="IPresignedUrlUploader"/> - a bare <see cref="HttpClient"/> PUT, the same shape a browser
/// (or `Ago.Chat.Worker.AttachmentThumbnailGenerator`) would make against a presigned URL.</summary>
public sealed class HttpPresignedUrlUploader : IPresignedUrlUploader
{
    private static readonly HttpClient Http = new();

    public async Task PutAsync(Uri url, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await Http.PutAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
