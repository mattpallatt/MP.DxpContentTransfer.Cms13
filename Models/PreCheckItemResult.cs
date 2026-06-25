namespace DxpContentTransfer.Cms13.Models;

public class PreCheckItemResult
{
    public string ContentId { get; set; }
    public Guid ContentGuid { get; set; }
    public string ContentName { get; set; }
    public PreCheckAction Action { get; set; }
    public Guid? TargetParentGuid { get; set; }
    public string TargetParentPath { get; set; }
    public Guid? NewGuid { get; set; }
    public string Notes { get; set; }
    public bool IsRootFallback { get; set; }
    public List<DependencyNode> Dependencies { get; set; } = new();
}

public class DependencyNode
{
    public string Name { get; set; }
    public string NodeType { get; set; } // "Block" | "Image" | "InlineImage"
    public string ContentGuid { get; set; }
    public List<DependencyNode> Children { get; set; } = new();
}
