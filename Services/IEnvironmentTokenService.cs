using DxpContentTransfer.Cms13.Models;

namespace DxpContentTransfer.Cms13.Services;

public interface IEnvironmentTokenService
{
    Task<string> GetTokenAsync(DxpEnvironmentConfig config);
}
