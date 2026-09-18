namespace DxpContentTransfer.Cms13.Models;

public class TransferResult
{
    public bool Success { get; set; }
    public int TransferredCount { get; set; }
    public List<TransferItemResult> Items { get; set; } = new();
    public string ErrorMessage { get; set; }
}

public class TransferItemResult
{
    public string ContentId { get; set; }
    public string ContentName { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    // The new CMS REST API has no numeric content-id concept — this is now the target's GUID-based
    // `key` (32-char hex). The admin deep-link the gadget builds from it
    // (#context=epi.cms.contentdata:///{key}) is unverified against the new API's admin shell —
    // this used to be a classic integer id; test the deep link after a transfer.
    public string TargetContentId { get; set; }
    public string TargetBaseUrl { get; set; }
    public List<string> DefaultedProperties { get; set; } = new();
    public List<string> FailedDependencyGuids { get; set; } = new();
}
