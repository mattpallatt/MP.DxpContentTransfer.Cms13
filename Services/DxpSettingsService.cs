using DxpContentTransfer.Cms13.Models;
using EPiServer.Data.Dynamic;

namespace DxpContentTransfer.Cms13.Services;

public class DxpSettingsService : IDxpSettingsService
{
    // DynamicDataStoreFactory uses IDatabaseExecutor which has thread affinity — calling it
    // from a background Task.Run thread after a previous request causes "executor not created
    // on current context" errors. Cache the settings after the first HTTP-thread load so that
    // background threads never touch DDS directly.
    private DxpTransferSettings _cached;
    private readonly object _lock = new();

    public DxpTransferSettings Get()
    {
        lock (_lock)
        {
            if (_cached != null) return _cached;
        }

        var store = DynamicDataStoreFactory.Instance.GetStore(typeof(DxpTransferSettings));
        var settings = store == null
            ? new DxpTransferSettings()
            : store.LoadAll<DxpTransferSettings>().FirstOrDefault() ?? new DxpTransferSettings();

        lock (_lock)
        {
            _cached = settings;
        }

        return settings;
    }

    public void Save(DxpTransferSettings settings)
    {
        var store = DynamicDataStoreFactory.Instance.GetStore(typeof(DxpTransferSettings))
                    ?? DynamicDataStoreFactory.Instance.CreateStore(typeof(DxpTransferSettings));

        var existing = store.LoadAll<DxpTransferSettings>().FirstOrDefault();
        if (existing != null)
            settings.Id = existing.Id;

        store.Save(settings);

        lock (_lock)
        {
            _cached = settings;
        }
    }
}
