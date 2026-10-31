$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'InstallerOptions.cs') -Raw
$checks = @'
namespace LeGoMyCursor.Setup
{
    internal enum SetupOperation { Probe = 1, Install = 2, Reinstall = 3, ApplyMode = 4, Uninstall = 5, Clean = 6 }
    internal static class Platform
    {
        internal const int ModeSimple = 1;
        internal const int ERROR_INVALID_PARAMETER = 87;
    }
    public static class OptionsCheck
    {
        public static void Run()
        {
            for (int mode = 0; mode <= 2; mode++)
            {
                var options = InstallerOptions.Parse(new[] { "/mode:" + mode });
                if (options.Operation != SetupOperation.ApplyMode || options.Mode != mode || !options.Headless)
                    throw new System.Exception("Mode action was not parsed correctly.");
            }
            if (InstallerOptions.Parse(new[] { "/probe" }).Operation != SetupOperation.Probe ||
                InstallerOptions.Parse(new string[0]).Operation.HasValue)
                throw new System.Exception("Existing startup actions changed.");
            var reinstall = InstallerOptions.Parse(new[] { "/reinstall" });
            if (reinstall.Operation != SetupOperation.Reinstall || !reinstall.Headless)
                throw new System.Exception("Headless reinstall was not parsed correctly.");
            InstallerOptions.Parse(new[] { "/uninstall", "/confirmed", "/temporary", "/parent:123" });
            foreach (var arguments in new[]
            {
                new[] { "/mode:" }, new[] { "/mode:3" }, new[] { "/mode:-1" },
                new[] { "/mode:+1" }, new[] { "/mode:01" }, new[] { "/mode:1 " },
                new[] { "/mode:1", "/mode:2" }, new[] { "/mode:1", "/probe" },
                new[] { "/uninstall", "/mode:1" }, new[] { "/mode:1", "/confirmed" },
                new[] { "/confirmed", "/mode:1" }, new[] { "/mode:1", "/temporary", "/parent:123" },
                new[] { "/mode:1", "/parent:123" }, new[] { "/mode:1", "unknown" },
                new[] { "/reinstall", "/mode:1" }, new[] { "/reinstall", "/confirmed" },
                new[] { "/reinstall", "/parent:123" }, new[] { "/reinstall", "/temporary" }
            })
            {
                bool rejected = false;
                try { InstallerOptions.Parse(arguments); }
                catch (System.ArgumentException) { rejected = true; }
                if (!rejected)
                    throw new System.Exception("Invalid mode arguments were accepted.");
            }
        }
    }
}
'@
Add-Type -TypeDefinition ($source + $checks)
[LeGoMyCursor.Setup.OptionsCheck]::Run()
Write-Output 'Installer mode arguments: valid modes and existing actions pass; malformed/conflicting options rejected.'
