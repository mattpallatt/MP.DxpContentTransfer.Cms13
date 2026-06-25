namespace DxpContentTransfer.Cms13.Models;

public enum PreCheckAction
{
    Overwrite,      // GUID exists on target, overwrite flag on
    CreateNew,      // GUID exists on target, overwrite flag off — new GUID, same parent
    Create,         // GUID not on target, parent resolved
    Unresolvable    // GUID not on target, parent cannot be found
}
