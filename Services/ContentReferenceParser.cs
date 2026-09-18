using EPiServer.Core;

namespace DxpContentTransfer.Cms13.Services;

public static class ContentReferenceParser
{
    // Parses an editor content id ("123", "123_456", or "123:eng") into a ContentReference,
    // returning EmptyReference when the id is missing or not numeric. Shared by the transfer
    // service and the gadget controller, which both used to inline this split-and-parse.
    public static ContentReference Parse(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ContentReference.EmptyReference;

        var parts = id.Split('_', ':');
        return int.TryParse(parts[0], out var contentId)
            ? new ContentReference(contentId)
            : ContentReference.EmptyReference;
    }
}
