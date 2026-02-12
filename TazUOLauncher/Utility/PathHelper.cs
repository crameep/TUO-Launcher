using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TazUOLauncher;

public static class PathHelper
{
    /// <summary>
    /// The directory where the launcher executable and native libraries live.
    /// Inside a macOS .app bundle this is Contents/MacOS/; otherwise same as LauncherPath.
    /// Used by the self-updater to replace launcher binaries in-place.
    /// </summary>
    public static string LauncherBinPath { get; } = AppDomain.CurrentDomain.BaseDirectory;

    public static string LauncherPath { get; set; } = GetDataPath();

    /// <summary>
    /// When running from a macOS .app bundle, returns the directory containing the .app
    /// so that TazUO client files aren't nested inside the bundle. This avoids SDL2's
    /// SDL_GetBasePath() misidentifying the client as part of the launcher's .app bundle.
    /// </summary>
    private static string GetDataPath()
    {
        string basePath = AppDomain.CurrentDomain.BaseDirectory;

        // Detect .app bundle: path contains "Something.app/Contents/MacOS/"
        int idx = basePath.IndexOf(".app/Contents/MacOS", StringComparison.Ordinal);
        if (idx >= 0)
        {
            // Find the start of "Something.app" by looking for the last separator before ".app"
            string upToApp = basePath.Substring(0, idx + 4); // includes ".app"
            string? parent = Path.GetDirectoryName(upToApp);
            if (parent != null)
                return parent + Path.DirectorySeparatorChar;
        }

        return basePath;
    }

    public static string ProfilesPath { get; set; } = Path.Combine(LauncherPath, "Profiles");

    public static string SettingsPath { get; set; } = Path.Combine(ProfilesPath, "Settings");

    /// <summary>
    /// This is the path to TazUO client, example: /home/TazUO Launcher/TazUO
    /// </summary>
    public static string ClientPath { get; set; } = Path.Combine(LauncherPath, CONSTANTS.CLIENT_DIRECTORY_NAME);

    /// <summary>
    /// Path to the .app bundle directory on macOS (e.g. TazUO/TazUO.app/).
    /// </summary>
    public static string ClientAppBundlePath => Path.Combine(ClientPath, CONSTANTS.NATIVE_EXECUTABLE_NAME + ".app");

    /// <summary>
    /// Path to Contents/MacOS/ inside the .app bundle on macOS.
    /// This is where the flat zip gets extracted on macOS.
    /// </summary>
    public static string ClientAppMacOSPath => Path.Combine(ClientAppBundlePath, "Contents", "MacOS");

    public static string NativeClientPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(ClientPath, CONSTANTS.NATIVE_EXECUTABLE_NAME + ".exe");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(ClientAppMacOSPath, CONSTANTS.NATIVE_EXECUTABLE_NAME);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Path.Combine(ClientPath, CONSTANTS.NATIVE_EXECUTABLE_NAME);
        }

        return string.Empty;
    }
    public static string ClientExecutablePath(bool returnExeOnly = false)
    {
        try
        {
            return NativePath(returnExeOnly);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to find path: {ex.Message}");
        }

        return string.Empty;
    }

    private static string NativePath(bool returnExeOnly)
    {
        string exeName;

        if (returnExeOnly || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            exeName = CONSTANTS.NATIVE_EXECUTABLE_NAME + ".exe";
            if (!File.Exists(Path.Combine(ClientPath, exeName)))
                exeName = CONSTANTS.CLASSIC_EXE_NAME + ".exe";

            return Path.Combine(ClientPath, exeName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // On macOS, the executable lives inside the .app bundle
            string appExe = Path.Combine(ClientAppMacOSPath, CONSTANTS.NATIVE_EXECUTABLE_NAME);
            if (File.Exists(appExe))
                return appExe;

            // Fallback: check for legacy flat install
            exeName = CONSTANTS.NATIVE_EXECUTABLE_NAME;
            if (!File.Exists(Path.Combine(ClientPath, exeName)))
                exeName = CONSTANTS.CLASSIC_EXE_NAME;

            return Path.Combine(ClientPath, exeName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            exeName = CONSTANTS.NATIVE_EXECUTABLE_NAME;
            if (!File.Exists(Path.Combine(ClientPath, exeName)))
                exeName = CONSTANTS.CLASSIC_EXE_NAME;

            return Path.Combine(ClientPath, exeName);
        }

        throw new PlatformNotSupportedException("Unsupported operating system.");
    }
}