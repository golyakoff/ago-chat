namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-160`: the one port <see cref="UseCases.SubmitLogoUpload.SubmitLogoUploadHandler"/> needs to push
/// bytes it already holds in memory to a presigned PUT URL - `clean-architecture.md`'s dependency rule
/// (`CLAUDE.md` rule 2): "no `HttpClient`... inside Domain or Application. Every external resource sits
/// behind a port declared in Application/Abstractions and implemented in an Infrastructure.* project."
/// <see cref="Ago.Platform.Abstractions.IFileStorage.CreateUploadAsync"/> only ever hands back the
/// presigned URL itself (the platform's own port is presign-only by design, `adr/0008`) - actually
/// performing the PUT against it is a second, genuinely infrastructure-shaped act
/// (`Ago.Chat.Worker.AttachmentThumbnailGenerator`'s own precedent for the identical shape, but that
/// class lives in a host project, which the dependency rule allows to do this directly; this handler
/// lives in Application, which may not).
/// </summary>
public interface IPresignedUrlUploader
{
    Task PutAsync(Uri url, string contentType, byte[] bytes, CancellationToken cancellationToken);
}
