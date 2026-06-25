using DxpContentTransfer.Cms13.Models;
using EPiServer.Data.Dynamic;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;

namespace DxpContentTransfer.Cms13.Initialization;

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class DxpContentTransferInitialization : IInitializableModule
{
    public void Initialize(InitializationEngine context)
    {
        DynamicDataStoreFactory.Instance.CreateStore(typeof(DxpTransferSettings));
    }

    public void Uninitialize(InitializationEngine context) { }
}
