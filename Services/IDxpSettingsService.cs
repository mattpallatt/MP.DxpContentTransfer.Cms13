using DxpContentTransfer.Cms13.Models;

namespace DxpContentTransfer.Cms13.Services;

public interface IDxpSettingsService
{
    DxpTransferSettings Get();
    void Save(DxpTransferSettings settings);
}
