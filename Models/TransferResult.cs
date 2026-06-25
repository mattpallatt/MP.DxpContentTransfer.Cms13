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
    public int? TargetContentId { get; set; }
    public string TargetBaseUrl { get; set; }
    public List<string> DefaultedProperties { get; set; } = new();
    public List<string> FailedDependencyGuids { get; set; } = new();
}
