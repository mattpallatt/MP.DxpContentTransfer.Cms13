namespace DxpContentTransfer.Cms13.Models;

public class PreCheckResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public List<PreCheckItemResult> Items { get; set; } = new();
    public int OverwriteCount => Items.Count(i => i.Action == PreCheckAction.Overwrite);
    public int CreateNewCount => Items.Count(i => i.Action == PreCheckAction.CreateNew);
    public int CreateCount => Items.Count(i => i.Action == PreCheckAction.Create);
    public int UnresolvableCount => Items.Count(i => i.Action == PreCheckAction.Unresolvable);
}
