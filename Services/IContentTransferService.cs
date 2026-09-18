using DxpContentTransfer.Cms13.Models;

namespace DxpContentTransfer.Cms13.Services;

public interface IContentTransferService
{
    Task<PreCheckResult> PreCheckAsync(string contentId, string targetEnvironment, bool includeChildren, bool overwriteMatchingIds, string destinationParentId = null, string destinationParentName = null);
    Task<TransferResult> TransferAsync(string contentId, string targetEnvironment, bool includeChildren, string sourceEnvironmentName, string transferStatus = "Published", List<PreCheckItemResult> plan = null, Action onItemComplete = null, IReadOnlyCollection<string> selectedLanguages = null);

    // Destination-tree picker: browsable target-side pages/folders, an automatic-placement
    // prediction, and the root-first path to auto-expand so that prediction is visible up front.
    Task<DestinationTreeRootResult> GetDestinationTreeRootAsync(string contentId, string targetEnvironment);
    Task<DestinationTreeChildrenResult> ListDestinationChildrenAsync(string targetEnvironment, string containerKey, string locale = null);
}
