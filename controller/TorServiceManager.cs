using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowDeploy.Controller;

internal sealed record TorWindowsService(string Name, string DisplayName, string State)
{
    public override string ToString() => $"{DisplayName} ({Name}) — {State}";
}

internal static class TorServiceManager
{
    public static TorWindowsService[] FindServices()
    {
        const string script = "Get-CimInstance Win32_Service | Where-Object { $_.Name -match 'tor' -or $_.DisplayName -match 'tor' } | Select-Object Name,DisplayName,State | ConvertTo-Json -Compress";
        var output = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"");
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<TorWindowsService>();

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };
        return rows.Select(row => new TorWindowsService(
            row.GetProperty("Name").GetString() ?? "",
            row.GetProperty("DisplayName").GetString() ?? "",
            row.GetProperty("State").GetString() ?? "Unknown"))
            .Where(x => x.Name.Length > 0)
            .ToArray();
    }

    public static void ControlService(string serviceName, string action)
    {
        if (action is not ("start" or "stop" or "restart"))
            throw new ArgumentOutOfRangeException(nameof(action));
        var escaped = serviceName.Replace("'", "''");
        var command = action switch
        {
            "start" => $"Start-Service -Name '{escaped}' -ErrorAction Stop",
            "stop" => $"Stop-Service -Name '{escaped}' -Force -ErrorAction Stop",
            _ => $"Restart-Service -Name '{escaped}' -Force -ErrorAction Stop"
        };
        var start = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the service-control process.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"PowerShell exited with code {process.ExitCode}.");
    }

    public static string? FindOnionHostname()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "tor", "meowdeploy-controller", "hostname");
        if (!File.Exists(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Tor", "meowdeploy-controller", "hostname");
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        return value.EndsWith(".onion", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    private static string Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            throw new TimeoutException("Tor service discovery timed out.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Process exited with code {process.ExitCode}." : error.Trim());
        return output;
    }
}
