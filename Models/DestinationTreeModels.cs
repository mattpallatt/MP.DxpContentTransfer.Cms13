namespace DxpContentTransfer.Cms13.Models;

// One browsable node in the target tree (a page or folder that exists on the target
// environment) — used by the gadget's "Place Under" picker.
public class DestinationTreeNode
{
    public string Key { get; set; }
    public string Name { get; set; }

    // Populated only in the fully preloaded tree (DestinationTreeRootResult.Tree) — null when this
    // node comes back from the single-level lazy children lookup instead.
    public List<DestinationTreeNode> Children { get; set; }
}

public class DestinationTreeRootResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }

    public string RootKey { get; set; }
    public string RootName { get; set; }

    // The site's default/master locale on the target, resolved once from the root node — passed
    // back on every subsequent children request so every name in the tree resolves consistently
    // in that locale instead of whichever locale happens to sort first per node.
    public string DefaultLocale { get; set; }

    // The automatic placement PreCheckAsync would resolve to if the editor never touches the
    // picker — same ancestor-matching logic as ResolveTargetParentAsync, computed once here so
    // the tree can show it up front and pre-expand to reveal it.
    public string DefaultParentKey { get; set; }
    public string DefaultParentName { get; set; }

    // Root-first chain of keys (root, ..., DefaultParentKey) the client expands automatically so
    // the predicted location is visible without the editor having to click through it manually.
    public List<string> ExpandPath { get; set; } = new();

    // The whole browsable tree, preloaded in one request so expand/collapse in the client is
    // instant (no further network round-trips) — see BuildDestinationSubtreeAsync. Capped at
    // MaxDestinationTreeNodes; Truncated is set when the cap was hit.
    public DestinationTreeNode Tree { get; set; }
    public bool Truncated { get; set; }
}

public class DestinationTreeChildrenResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public List<DestinationTreeNode> Children { get; set; } = new();
}
