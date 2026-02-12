using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TazUOLauncher;

internal static class LauncherSelfUpdater
{
    // Directories and files that belong to the user, not the launcher distribution.
    // These are never renamed to *.old during an update. Note: only files with a
    // counterpart in the staging directory are touched, so unlisted user files are
    // also safe — this is belt-and-suspenders protection.
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcherdata.json",
        "Profiles",
        CONSTANTS.CLIENT_DIRECTORY_NAME, // "TazUO" — the game client lives here
    };

    /// <summary>
    /// Downloads the latest launcher release, replaces the current launcher files
    /// using rename-old / extract-new, then launches the new executable.
    /// Returns true if the update succeeded (caller should exit gracefully).
    /// Returns false if the update failed (caller should offer manual fallback).
    /// </summary>
    public static async Task<bool> DownloadAndApplyUpdate(DownloadProgress progress)
    {
        string launcherDir = PathHelper.LauncherBinPath;
        string tempZip = string.Empty;
        string stagingDir = string.Empty;
        var renamedFiles = new List<(string original, string oldPath)>();
        var movedFiles = new List<string>();

        try
        {
            // ── 1. Locate the platform-specific asset ──────────────────────
            if (!UpdateHelper.HaveData(ReleaseChannel.LAUNCHER))
                return false;

            var releaseData = UpdateHelper.ReleaseData[ReleaseChannel.LAUNCHER];
            if (releaseData?.assets == null)
                return false;

            string platformZipName = PlatformHelper.GetPlatformZipName();
            var asset = releaseData.assets.FirstOrDefault(
                a => a.name != null && a.name.EndsWith(platformZipName) && a.browser_download_url != null);

            if (asset?.browser_download_url == null)
                return false;

            Console.WriteLine($"[SelfUpdate] Downloading {asset.name} from {asset.browser_download_url}");

            // ── 2. Download ZIP to temp file ───────────────────────────────
            tempZip = Path.GetTempFileName();
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var httpClient = new HttpClient())
            {
                await httpClient.DownloadAsync(asset.browser_download_url, fs, progress);
            }

            // ── 3. Extract to a staging directory ──────────────────────────
            stagingDir = Path.Combine(Path.GetTempPath(), "TazUOLauncher_update_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(tempZip, stagingDir, true);

            // ── 4. Rename existing launcher files to *.old ─────────────────
            renamedFiles = RenameExistingFiles(launcherDir, stagingDir);

            // ── 5. Move new files from staging into launcher directory ──────
            movedFiles = MoveNewFiles(stagingDir, launcherDir);

            // ── 6. Set executable permissions on Unix ───────────────────────
            SetUnixExecutablePermissions(launcherDir);

            // ── 7. Launch the new process ──────────────────────────────────
            string newExePath = GetLauncherExePath(launcherDir);
            if (!string.IsNullOrEmpty(newExePath) && File.Exists(newExePath))
            {
                Console.WriteLine($"[SelfUpdate] Launching updated launcher: {newExePath}");
                Process.Start(new ProcessStartInfo(newExePath) { WorkingDirectory = launcherDir });
                return true; // Caller should exit gracefully
            }

            // Files are in place but we can't find the exe — still a success,
            // user can manually restart.
            Console.WriteLine("[SelfUpdate] Warning: Could not locate new launcher executable after update.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SelfUpdate] Update failed: {ex}");

            // ── Rollback: remove newly-moved files, rename *.old back ──────
            Rollback(renamedFiles, movedFiles);
            return false;
        }
        finally
        {
            // ── Clean up temp files ────────────────────────────────────────
            CleanTemp(tempZip, stagingDir);
        }
    }

    /// <summary>
    /// Renames existing launcher files that will be replaced by the new update.
    /// Only renames files that exist in the staging directory (i.e., files the
    /// new release will overwrite). Skips user data directories/files.
    /// </summary>
    private static List<(string original, string oldPath)> RenameExistingFiles(string launcherDir, string stagingDir)
    {
        var renamed = new List<(string original, string oldPath)>();

        // Rename files in the launcher root that have a counterpart in staging
        foreach (string stagingFile in Directory.EnumerateFiles(stagingDir))
        {
            string fileName = Path.GetFileName(stagingFile);
            if (ShouldSkip(fileName))
                continue;

            string existingFile = Path.Combine(launcherDir, fileName);
            if (File.Exists(existingFile))
            {
                string oldPath = existingFile + ".old";
                try
                {
                    // Remove any leftover .old from a previous failed update
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);

                    File.Move(existingFile, oldPath);
                    renamed.Add((existingFile, oldPath));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SelfUpdate] Failed to rename {existingFile}: {ex.Message}");
                }
            }
        }

        // Rename subdirectories that have a counterpart in staging
        foreach (string stagingSubDir in Directory.EnumerateDirectories(stagingDir))
        {
            string dirName = Path.GetFileName(stagingSubDir);
            if (ShouldSkip(dirName))
                continue;

            string existingDir = Path.Combine(launcherDir, dirName);
            if (Directory.Exists(existingDir))
            {
                string oldPath = existingDir + ".old";
                try
                {
                    if (Directory.Exists(oldPath))
                        Directory.Delete(oldPath, true);

                    Directory.Move(existingDir, oldPath);
                    renamed.Add((existingDir, oldPath));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SelfUpdate] Failed to rename directory {existingDir}: {ex.Message}");
                }
            }
        }

        return renamed;
    }

    /// <summary>
    /// Moves all files and directories from staging into the launcher directory.
    /// Returns a list of destination paths for rollback purposes.
    /// </summary>
    private static List<string> MoveNewFiles(string stagingDir, string launcherDir)
    {
        var moved = new List<string>();

        foreach (string file in Directory.EnumerateFiles(stagingDir))
        {
            string fileName = Path.GetFileName(file);
            string dest = Path.Combine(launcherDir, fileName);
            File.Move(file, dest, true);
            moved.Add(dest);
        }

        foreach (string dir in Directory.EnumerateDirectories(stagingDir))
        {
            string dirName = Path.GetFileName(dir);
            string dest = Path.Combine(launcherDir, dirName);
            MoveDirectoryContents(dir, dest, moved);
        }

        return moved;
    }

    /// <summary>
    /// Recursively moves directory contents. Creates destination if needed.
    /// Tracks moved files for rollback.
    /// </summary>
    private static void MoveDirectoryContents(string sourceDir, string destDir, List<string> moved)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            string dest = Path.Combine(destDir, fileName);
            File.Move(file, dest, true);
            moved.Add(dest);
        }

        foreach (string dir in Directory.EnumerateDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(dir);
            MoveDirectoryContents(dir, Path.Combine(destDir, dirName), moved);
        }
    }

    private static void SetUnixExecutablePermissions(string launcherDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        try
        {
            // Set +x on the main launcher executable
            string exePath = GetLauncherExePath(launcherDir);
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                File.SetUnixFileMode(exePath, mode);

            // Also set +x on any native shared libraries
            foreach (string soFile in Directory.EnumerateFiles(launcherDir, "*.so", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(soFile, mode); }
                catch { /* best effort for individual libs */ }
            }
            foreach (string dylibFile in Directory.EnumerateFiles(launcherDir, "*.dylib", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(dylibFile, mode); }
                catch { /* best effort for individual libs */ }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SelfUpdate] Failed to set Unix permissions: {ex.Message}");
        }
    }

    private static string GetLauncherExePath(string launcherDir)
    {
        if (PlatformHelper.IsWindows)
        {
            string exe = Path.Combine(launcherDir, "TazUOLauncher.exe");
            if (File.Exists(exe)) return exe;
        }
        else
        {
            // On Linux/Mac, the self-contained publish produces an executable without extension
            string exe = Path.Combine(launcherDir, "TazUOLauncher");
            if (File.Exists(exe)) return exe;
        }

        // Fallback: use the current process path (may still be valid if rename failed)
        return Environment.ProcessPath ?? string.Empty;
    }

    private static void Rollback(List<(string original, string oldPath)> renamedFiles, List<string> movedFiles)
    {
        // First, remove any new files that were moved into place
        foreach (string movedFile in movedFiles)
        {
            try
            {
                if (File.Exists(movedFile))
                    File.Delete(movedFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SelfUpdate] Rollback: failed to remove new file {movedFile}: {ex.Message}");
            }
        }

        // Then, rename *.old files back to their original names
        foreach (var (original, oldPath) in renamedFiles)
        {
            try
            {
                if (File.Exists(oldPath))
                {
                    if (File.Exists(original))
                        File.Delete(original);
                    File.Move(oldPath, original);
                }
                else if (Directory.Exists(oldPath))
                {
                    if (Directory.Exists(original))
                        Directory.Delete(original, true);
                    Directory.Move(oldPath, original);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SelfUpdate] Rollback failed for {original}: {ex.Message}");
            }
        }
    }

    private static void CleanTemp(string tempZip, string stagingDir)
    {
        try { if (!string.IsNullOrEmpty(tempZip) && File.Exists(tempZip)) File.Delete(tempZip); }
        catch { /* best effort */ }

        try { if (!string.IsNullOrEmpty(stagingDir) && Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
        catch { /* best effort */ }
    }

    private static bool ShouldSkip(string name) => SkipNames.Contains(name);
}
