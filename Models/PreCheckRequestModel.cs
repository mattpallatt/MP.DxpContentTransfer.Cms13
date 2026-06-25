namespace DxpContentTransfer.Cms13.Models;

public class PreCheckRequestModel
{
    public string ContentId { get; set; }
    public string TargetEnvironment { get; set; }
    public bool IncludeChildren { get; set; }
    public bool OverwriteMatchingIds { get; set; }
}
