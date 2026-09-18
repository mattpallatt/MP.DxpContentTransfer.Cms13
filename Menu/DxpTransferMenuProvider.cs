using EPiServer.Shell.Navigation;

namespace DxpContentTransfer.Cms13.Menu;

[MenuProvider]
public class DxpTransferMenuProvider : IMenuProvider
{
    public IEnumerable<MenuItem> GetMenuItems()
    {
        return new[]
        {
            new UrlMenuItem(
                "DXP Content Transfer",
                MenuPaths.Global + "/cms/admin/tools/dxp.transfer",
                "/EPiServer/DxpContentTransfer/Admin/Settings")
            {
                IsAvailable = _ => true,
                SortIndex = SortIndex.Last + 1
            }
        };
    }
}
