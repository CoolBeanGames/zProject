using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace PromptQueue.App;

internal static class OperatorPathInstaller
{
    private const string InstallSwitch = "--install-operator-path";
    private const int ErrorCancelled = 1223;
    private const int HwndBroadcast = 0xffff;
    private const int WmSettingChange = 0x001a;
    private const int SmtoAbortIfHung = 0x0002;

    internal readonly record struct InstallResult(bool Success, string? Error)
    {
        public static InstallResult Ok() => new(true, null);
        public static InstallResult Failed(string error) => new(false, error);
    }

    /// <summary>
    /// Runs the small, elevation-capable installer mode before the normal
    /// single-instance guard is acquired.
    /// </summary>
    public static bool TryRunInstallerMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], InstallSwitch, StringComparison.Ordinal))
            return false;

        if (args.Length != 3)
        {
            exitCode = 2;
            return true;
        }

        try
        {
            Install(args[1], args[2]);
        }
        catch
        {
            exitCode = 1;
        }

        return true;
    }

    public static InstallResult EnsureInstalled()
    {
        string operatorExe = Path.Combine(AppContext.BaseDirectory, "zProject_operator.exe");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string binDirectory = Path.Combine(localAppData, "zProject", "bin");

        try
        {
            Install(operatorExe, binDirectory);
            return InstallResult.Ok();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return TryElevatedInstall(operatorExe, binDirectory);
        }
        catch (Exception ex)
        {
            return InstallResult.Failed(ex.Message);
        }
    }

    private static void Install(string operatorExe, string binDirectory)
    {
        operatorExe = Path.GetFullPath(operatorExe);
        binDirectory = Path.GetFullPath(binDirectory);

        if (!File.Exists(operatorExe))
            throw new FileNotFoundException("The packaged zProject_operator.exe could not be found.", operatorExe);

        Directory.CreateDirectory(binDirectory);

        string shimPath = Path.Combine(binDirectory, "operator.cmd");
        string escapedOperatorPath = operatorExe.Replace("%", "%%", StringComparison.Ordinal);
        string shimContents = $"@\"{escapedOperatorPath}\" %*{Environment.NewLine}";

        if (!File.Exists(shimPath) ||
            !string.Equals(File.ReadAllText(shimPath), shimContents, StringComparison.Ordinal))
        {
            File.WriteAllText(shimPath, shimContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        if (!ContainsPath(userPath, binDirectory))
        {
            string updatedUserPath = string.IsNullOrWhiteSpace(userPath)
                ? binDirectory
                : $"{binDirectory};{userPath}";
            Environment.SetEnvironmentVariable("Path", updatedUserPath, EnvironmentVariableTarget.User);
            BroadcastEnvironmentChange();
        }

        string processPath = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        if (!ContainsPath(processPath, binDirectory))
            Environment.SetEnvironmentVariable("Path", $"{binDirectory};{processPath}");
    }

    private static InstallResult TryElevatedInstall(string operatorExe, string binDirectory)
    {
        string? appExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appExe))
            return InstallResult.Failed("Windows denied access and zProject could not locate itself to request administrator permission.");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appExe,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(InstallSwitch);
            startInfo.ArgumentList.Add(operatorExe);
            startInfo.ArgumentList.Add(binDirectory);

            using Process? installer = Process.Start(startInfo);
            if (installer == null)
                return InstallResult.Failed("Windows did not start the operator PATH installer.");

            installer.WaitForExit();
            return installer.ExitCode == 0
                ? InstallResult.Ok()
                : InstallResult.Failed("The elevated operator PATH installer did not complete successfully.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return InstallResult.Failed("Administrator permission was declined. You can still run zProject_operator.exe from the release folder.");
        }
        catch (Exception ex)
        {
            return InstallResult.Failed(ex.Message);
        }
    }

    private static bool ContainsPath(string pathValue, string candidate)
    {
        string normalizedCandidate = NormalizePath(candidate);
        return pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(NormalizePath(entry), normalizedCandidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path) =>
        path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            IntPtr.Zero,
            "Environment",
            SmtoAbortIfHung,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        int hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out IntPtr result);
}
