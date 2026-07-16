namespace Smx.Functions.Common;

// Anchors relative config paths to the deployed content root. App settings carry relative paths
// ("Sds/Config/suppliers.allowlist.json"); resolving them against the process CWD crashed the
// first live SdsSweep on Flex Consumption, whose CWD is a standby placeholder directory
// ('/tmp/functions/standby/wwwroot'). AppContext.BaseDirectory always points at the real
// content root. Absolute paths pass through untouched (Path.Combine drops the base for them).
public static class ContentRoot
{
    public static string Resolve(string path) => Path.Combine(AppContext.BaseDirectory, path);
}
