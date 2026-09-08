using Ago.Chat.Application.UseCases.CreateAttachment;

namespace Ago.Chat.Application.Tests.UseCases.CreateAttachment;

/// <summary>`23-81`'s own content is a number, not a mechanism (`CreateAttachmentHandler`'s size
/// check and the presigned PUT's signed `Content-Length` were both already proven by `5-13`) - this
/// is the one behaviour that actually changed, so it gets its own assertion rather than being
/// implied by <see cref="CreateAttachmentHandlerTests"/>'s use of <c>options.MaxSizeBytes + 1</c>,
/// which would pass unchanged against any default.</summary>
public class AttachmentOptionsTests
{
    [Fact]
    public void MaxSizeBytes_DefaultsToFiveMebibytes()
    {
        var options = new AttachmentOptions();

        Assert.Equal(5 * 1024 * 1024, options.MaxSizeBytes);
    }
}

