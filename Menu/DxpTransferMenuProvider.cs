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
                "/global/cms/admin/tools/dxp.transfer.cms13",
                "/EPiServer/DxpContentTransfer/Admin/Settings")
            {
                IsAvailable = _ => true,
                SortIndex = SortIndex.Last + 1
            }
        };
    }
}
