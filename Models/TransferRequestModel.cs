namespace DxpContentTransfer.Cms13.Models;

public class TransferRequestModel
{
    public string ContentId { get; set; }
    public string TargetEnvironment { get; set; }
    public bool IncludeChildren { get; set; }
    public bool OverwriteMatchingIds { get; set; }
    public string TransferStatus { get; set; } = "Published";
    public List<PreCheckItemResult> Plan { get; set; }
    public string JobId { get; set; }
}
