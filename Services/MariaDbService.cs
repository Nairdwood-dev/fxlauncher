using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Nairdwood.Launcher.Services;

public enum MariaDbState
{
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Unknown
}

public sealed record MariaDbStatus(string? ServiceName, string DisplayName, MariaDbState State)
{
    public bool IsInstalled => !string.IsNullOrWhiteSpace(ServiceName);
}

public sealed class MariaDbService
{
    public const string DownloadUrl = "https://mariadb.org/download/";

    public async Task<MariaDbStatus> GetStatusAsync()
    {
        var installed = FindInstalledService();
        if (installed is null)
            return new MariaDbStatus(null, "MariaDB", MariaDbState.NotInstalled);

        var state = await QueryStateAsync(installed.Value.Name);
        return new MariaDbStatus(installed.Value.Name, installed.Value.DisplayName, state);
    }

    public async Task ToggleAsync(MariaDbStatus status)
    {
        if (!status.IsInstalled)
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            return;
        }

        var action = status.State is MariaDbState.Running or MariaDbState.StartPending ? "stop" : "start";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = $"{action} \"{status.ServiceName}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        if (process is not null) await process.WaitForExitAsync();
    }

    private static (string Name, string DisplayName)? FindInstalledService()
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return null;

        var matches = new List<(string Name, string DisplayName, int Priority)>();
        foreach (var serviceName in services.GetSubKeyNames())
        {
            using var service = services.OpenSubKey(serviceName);
            var displayName = service?.GetValue("DisplayName") as string ?? serviceName;
            var imagePath = service?.GetValue("ImagePath") as string ?? string.Empty;

            var isMariaDb = serviceName.Contains("mariadb", StringComparison.OrdinalIgnoreCase)
                            || displayName.Contains("mariadb", StringComparison.OrdinalIgnoreCase)
                            || imagePath.Contains("mariadb", StringComparison.OrdinalIgnoreCase);
            if (!isMariaDb) continue;

            var priority = serviceName.Equals("MariaDB", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            matches.Add((serviceName, displayName, priority));
        }

        return matches.OrderBy(match => match.Priority).ThenBy(match => match.Name).Select(match =>
            ((string Name, string DisplayName)?)(match.Name, match.DisplayName)).FirstOrDefault();
    }

    private static async Task<MariaDbState> QueryStateAsync(string serviceName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = $"query \"{serviceName}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });

        if (process is null) return MariaDbState.Unknown;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return MariaDbState.Running;
        if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return MariaDbState.Stopped;
        if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return MariaDbState.StartPending;
        if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return MariaDbState.StopPending;
        return MariaDbState.Unknown;
    }
}
