using Smx.Functions.Common;
using Xunit;

// Regression guard for the live 2026-07-16 SdsSweep crash: config paths are relative
// ("Sds/Config/suppliers.allowlist.json") and File.ReadAllText resolved them against the
// process CWD — which on Flex Consumption is a standby placeholder dir, NOT the content root
// (DirectoryNotFoundException: '/tmp/functions/standby/wwwroot/Sds/Config/...'). Every config
// read must anchor relative paths to AppContext.BaseDirectory instead.
public class ContentRootTests
{
    [Fact]
    public void Resolve_anchors_relative_paths_to_the_app_base_directory()
    {
        var resolved = ContentRoot.Resolve("Sds/Config/suppliers.allowlist.json");
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Sds/Config/suppliers.allowlist.json"),
            resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void Resolve_leaves_absolute_paths_untouched()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\etc\allowlist.json" : "/etc/allowlist.json";
        Assert.Equal(abs, ContentRoot.Resolve(abs));
    }

    [Fact]
    public void FromFile_loads_a_relative_path_that_exists_only_under_the_base_directory()
    {
        // Simulate the deployed layout: the file lives under BaseDirectory; the caller passes
        // the same relative path the app settings carry.
        var rel = Path.Combine("Resources", $"cwd-guard-{Guid.NewGuid():N}.json");
        var full = Path.Combine(AppContext.BaseDirectory, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, """
          [ { "supplier":"X","domain":"x.example","priority":1,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://x.example/{cas}.pdf" } ]
        """);
        try
        {
            var allow = Smx.Functions.Sds.Sourcing.AllowlistProvider.FromFile(rel);
            Assert.Contains("x.example", allow.Domains);

            File.WriteAllText(full, """
              [ { "sourceId":"s1","name":"S1","domain":"y.example","parser":"genericCsv",
                  "url":"https://y.example/list.csv","enabled":true } ]
            """);
            var reg = Smx.Functions.Reg.Sourcing.RegRegistryProvider.FromFile(rel);
            Assert.Contains("y.example", reg.Domains);
        }
        finally { File.Delete(full); }
    }
}
