using System;
using System.IO;

namespace TazUOLauncher;

internal static class SelfUpdateCleanup
{
    /// <summary>
    /// Scans the launcher directory for *.old files left over from a previous
    /// self-update and deletes them. Best-effort — failures are logged but never
    /// propagated so startup is never blocked.
    /// </summary>
    public static void CleanOldFiles()
    {
        try
        {
            string launcherDir = PathHelper.LauncherBinPath;

            foreach (string file in Directory.EnumerateFiles(launcherDir, "*.old", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SelfUpdateCleanup] Failed to delete {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SelfUpdateCleanup] Error scanning for old files: {ex.Message}");
        }
    }
}
