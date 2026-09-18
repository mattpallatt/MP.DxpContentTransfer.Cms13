namespace DxpContentTransfer.Cms13.Models;

public class PreCheckRequestModel
{
    public string ContentId { get; set; }
    public string TargetEnvironment { get; set; }
    public bool IncludeChildren { get; set; }
    public bool OverwriteMatchingIds { get; set; }

    // Manual "Place Under" override from the destination-tree picker — a target content key.
    // Null/empty means automatic (ResolveTargetParentAsync's ancestor-matching result). Applies
    // only to the top-level item being transferred; subpages/dependencies keep resolving their
    // placement relative to their own parent within the batch, so the existing structure is
    // preserved under whichever root the editor picked.
    public string DestinationParentId { get; set; }
    public string DestinationParentName { get; set; }
}
