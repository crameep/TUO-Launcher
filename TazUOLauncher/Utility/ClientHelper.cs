using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TazUOLauncher;

internal static class ClientHelper
{
    private static ClientVersionInfo localClientVersion = GetInstalledVersion();

    public static ClientVersionInfo LocalClientVersion { get => localClientVersion; set { localClientVersion = GetInstalledVersion(); } }

    /// <summary>
    /// This will cleanup TazUO files when swapping channels
    /// </summary>
    public static void CleanUpClientFiles()
    {
        string[] keepDirectories = new[] { "Data", "LegionScripts", "Fonts", "ExternalImages" };

        try
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(PathHelper.ClientPath);

            if (!directoryInfo.Exists) return;

            var subDirectories = directoryInfo.GetDirectories();
            foreach (var subDirectory in subDirectories)
            {
                // On macOS, also clean the .app bundle itself (it gets recreated on install)
                if (PlatformHelper.IsMac && subDirectory.Name.EndsWith(".app"))
                {
                    subDirectory.Delete(true);
                    continue;
                }
                if (keepDirectories.Contains(subDirectory.Name)) continue;
                subDirectory.Delete(true);
            }

            var files = directoryInfo.GetFiles();
            foreach (var file in files)
                file.Delete();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up client files: {ex}");
        }
    }
    
    public static bool ExecutableExists(bool checkExeOnly = false)
    {
        return File.Exists(PathHelper.ClientExecutablePath(checkExeOnly));
    }
    public static void TrySetPlusXUnix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // For example, set the executable bit for owner, group, and others.
                // Unix permissions: 0o755 => read/write/execute for owner, read/execute for group and others.
                // Note: The API uses a numeric type, so make sure you supply the correct mode.
                File.SetUnixFileMode(PathHelper.ClientExecutablePath(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting file mode: {ex}");
            }
        }
    }
    private static ClientVersionInfo GetInstalledVersion()
    {
        var versionTxt = Path.Combine(PathHelper.ClientPath, "v.txt");
        if (File.Exists(versionTxt))
        {
            try
            {
                var version = File.ReadAllText(versionTxt).Trim();
                if (!string.IsNullOrEmpty(version))
                    return ClientVersionInfo.Parse(version);
            }
            catch { }
        }

        if (File.Exists(PathHelper.ClientExecutablePath(true)))
        {
            var asmVersion = AssemblyName.GetAssemblyName(PathHelper.ClientExecutablePath(true)).Version;
            if (asmVersion != null)
                return ClientVersionInfo.Parse($"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}");
        }

        return ClientVersionInfo.Empty;
    }
}