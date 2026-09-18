using DxpContentTransfer.Cms13.Models;

namespace DxpContentTransfer.Cms13.Services;

// Diagnostic probe for a single environment's API configuration, surfaced by the
// "Test connection" button on the admin settings page. Verifies the three things
// that silently break a transfer: the OAuth token endpoint, the granted scope, and
// that the Content Management API is actually exposed on the environment.
public interface IEnvironmentHealthService
{
    Task<EnvironmentHealthResult> CheckAsync(DxpEnvironmentConfig config);
}

public readonly record struct EnvironmentHealthResult(bool Ok, string Message)
{
    public static EnvironmentHealthResult Pass(string message) => new(true, message);
    public static EnvironmentHealthResult Fail(string message) => new(false, message);
}
