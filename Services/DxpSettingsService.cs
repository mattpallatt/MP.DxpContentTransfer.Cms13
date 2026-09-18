using DxpContentTransfer.Cms13.Models;
using EPiServer.Data.Dynamic;

namespace DxpContentTransfer.Cms13.Services;

public class DxpSettingsService : IDxpSettingsService
{
    // DynamicDataStoreFactory uses IDatabaseExecutor which has thread affinity — calling it
    // from a background Task.Run thread after a previous request causes "executor not created
    // on current context" errors. The cache serves two purposes:
    //   1. Background threads (transfer Task.Run) never touch DDS — they always find a warm cache
    //      because the HTTP-thread endpoint pre-warms it immediately before Task.Run fires.
    //   2. Multi-node DXP deployments: a short TTL ensures all nodes pick up settings saved on
    //      any other node within 30 seconds. Indefinite caching caused the gadget picker to show
    //      stale environments after an admin updated settings on a different node.
    private DxpTransferSettings _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private readonly object _lock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public DxpTransferSettings Get()
    {
        lock (_lock)
        {
            if (_cached != null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;
        }

        var store = DynamicDataStoreFactory.Instance.GetStore(typeof(DxpTransferSettings));
        var settings = store == null
            ? new DxpTransferSettings()
            : store.LoadAll<DxpTransferSettings>().FirstOrDefault() ?? new DxpTransferSettings();

        lock (_lock)
        {
            _cached = settings;
            _cachedAt = DateTimeOffset.UtcNow;
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
            _cachedAt = DateTimeOffset.UtcNow;
        }
    }
}
