using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PromptQueue.Server;

/// <summary>
/// Thin wrapper over the <c>tailscale</c> CLI so the task server can publish its
/// local HTTP port to the internet with Tailscale Funnel (ZP-68). Everything
/// runs without opening a window; if Tailscale is not installed the server just
/// carries on serving localhost and prints how to set it up.
/// </summary>
internal static class Tailscale
{
    private static readonly Regex TsUrl =
        new(@"https://[a-z0-9-]+(?:\.[a-z0-9-]+)+\.ts\.net\S*", RegexOptions.IgnoreCase);

    /// <summary>Starts a background funnel for <paramref name="port"/>; returns the public URL or null.</summary>
    public static string? StartFunnel(int port)
    {
        var exe = FindExe();
        if (exe == null)
        {
            Console.WriteLine();
            Console.WriteLine("  Tailscale was not found. To reach this from your phone:");
            Console.WriteLine("    1. Install Tailscale on this PC and your phone (https://tailscale.com/download)");
            Console.WriteLine("    2. Sign both into the same tailnet");
            Console.WriteLine("    3. Once: tailscale funnel --bg " + port);
            Console.WriteLine("  Serving localhost only for now.");
            Console.WriteLine();
            return null;
        }

        // Newer CLIs: `tailscale funnel --bg <port>`. Older ones accept the same.
        var (ok, output) = Run(exe, $"funnel --bg {port}", 25000);
        if (!ok)
        {
            // Fall back to the explicit target form.
            (ok, output) = Run(exe, $"funnel --bg http://localhost:{port}", 25000);
        }

        var url = TsUrl.Match(output).Value;
        if (string.IsNullOrEmpty(url))
        {
            // Ask the running funnel what it is serving.
            var (_, status) = Run(exe, "funnel status", 10000);
            url = TsUrl.Match(status).Value;
        }

        if (!ok && string.IsNullOrEmpty(url))
        {
            Console.WriteLine();
            Console.WriteLine("  Could not start Tailscale Funnel:");
            foreach (var line in output.Split('\n'))
                if (line.Trim().Length > 0)
                    Console.WriteLine("    " + line.TrimEnd());
            Console.WriteLine("  (Funnel must be enabled for your tailnet in the admin console.)");
            Console.WriteLine("  Serving localhost only for now.");
            Console.WriteLine();
            return null;
        }

        return string.IsNullOrEmpty(url) ? null : url.TrimEnd('/') + "/";
    }

    /// <summary>Tears the funnel down again.</summary>
    public static void StopFunnel(int port)
    {
        var exe = FindExe();
        if (exe == null)
            return;
        var (ok, _) = Run(exe, $"funnel --bg off", 15000);
        if (!ok)
            Run(exe, $"funnel {port} off", 15000);
    }

    private static string? FindExe()
    {
        var candidates = new[]
        {
            "tailscale",
            "tailscale.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            @"C:\Program Files\Tailscale\tailscale.exe",
        };

        foreach (var c in candidates)
        {
            try
            {
                if (c.Contains('\\') && !File.Exists(c))
                    continue;
                var (ok, _) = Run(c, "version", 5000);
                if (ok)
                    return c;
            }
            catch
            {
                // try the next candidate
            }
        }
        return null;
    }

    private static (bool ok, string output) Run(string exe, string args, int timeoutMs)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(true); } catch { }
                return (false, "timed out");
            }
            var text = (stdout.Result + "\n" + stderr.Result).Trim();
            return (proc.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
