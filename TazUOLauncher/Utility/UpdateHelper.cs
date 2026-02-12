using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace TazUOLauncher;

internal static class UpdateHelper
{
    private const string BRANCH_TAG_PREFIX = "branch-";
    private static readonly TimeSpan BranchCacheTtl = TimeSpan.FromMinutes(5);

    public static ConcurrentDictionary<ReleaseChannel, GitHubReleaseData> ReleaseData = new ConcurrentDictionary<ReleaseChannel, GitHubReleaseData>();

    private static List<GitHubReleaseData>? _cachedBranchReleases;
    private static DateTime _branchCacheTimestamp = DateTime.MinValue;

    public static bool HaveData(ReleaseChannel channel) { return ReleaseData.ContainsKey(channel) && ReleaseData[channel] != null; }

    public static async Task GetAllReleaseData()
    {
        List<Task> all = new List<Task>(){
            TryGetReleaseData(ReleaseChannel.DEV),
            Task.Delay(500),
            TryGetReleaseData(ReleaseChannel.MAIN),
            Task.Delay(500),
            TryGetReleaseData(ReleaseChannel.LAUNCHER),
        };

        await Task.WhenAll(all);

        // Fetch branch after other channels so MAIN is available for fallback
        await FetchAndCacheBranchRelease();
    }

    public static async Task<List<GitHubReleaseData>> GetBranchReleases()
    {
        if (_cachedBranchReleases != null && DateTime.UtcNow - _branchCacheTimestamp < BranchCacheTtl)
            return _cachedBranchReleases;

        string url = CONSTANTS.BRANCH_BUILDS_API_URL + "?per_page=100";

        HttpRequestMessage restApi = new HttpRequestMessage()
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri(url),
        };
        restApi.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        restApi.Headers.Add("User-Agent", "Public");

        try
        {
            var httpClient = new HttpClient();
            string jsonResponse = await httpClient.Send(restApi).Content.ReadAsStringAsync();
            var allReleases = JsonSerializer.Deserialize<GitHubReleaseData[]>(jsonResponse);

            if (allReleases != null)
            {
                _cachedBranchReleases = allReleases
                    .Where(r => r.tag_name != null && r.tag_name.StartsWith(BRANCH_TAG_PREFIX))
                    .ToList();
            }
            else
            {
                _cachedBranchReleases = new List<GitHubReleaseData>();
            }

            _branchCacheTimestamp = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            _cachedBranchReleases ??= new List<GitHubReleaseData>();
        }

        return _cachedBranchReleases;
    }

    public static async Task<List<string>> GetBranchNames()
    {
        var releases = await GetBranchReleases();
        return releases
            .Where(r => !string.IsNullOrEmpty(r.tag_name) && r.tag_name.Length > BRANCH_TAG_PREFIX.Length)
            .Select(r => r.tag_name!.Substring(BRANCH_TAG_PREFIX.Length))
            .ToList();
    }

    public static async Task<GitHubReleaseData?> GetBranchReleaseData(string branchName)
    {
        var releases = await GetBranchReleases();
        string tagName = BRANCH_TAG_PREFIX + branchName;
        return releases.FirstOrDefault(r => r.tag_name == tagName);
    }

    private static async Task FetchAndCacheBranchRelease()
    {
        string selectedBranch = LauncherSettings.GetLauncherSaveFile.SelectedBranch;
        if (string.IsNullOrEmpty(selectedBranch))
            return;

        var branchRelease = await GetBranchReleaseData(selectedBranch);

        if (branchRelease != null)
        {
            if (!ReleaseData.TryAdd(ReleaseChannel.BRANCH, branchRelease))
                ReleaseData[ReleaseChannel.BRANCH] = branchRelease;
        }
        else
        {
            Console.WriteLine($"Branch build '{selectedBranch}' not found. Falling back to MAIN channel.");
            if (HaveData(ReleaseChannel.MAIN))
            {
                if (!ReleaseData.TryAdd(ReleaseChannel.BRANCH, ReleaseData[ReleaseChannel.MAIN]))
                    ReleaseData[ReleaseChannel.BRANCH] = ReleaseData[ReleaseChannel.MAIN];
            }
        }
    }

    private static async Task<GitHubReleaseData?> TryGetReleaseData(ReleaseChannel channel)
    {
        string url;

        switch (channel)
        {
            case ReleaseChannel.MAIN:
                url = CONSTANTS.MAIN_CHANNEL_RELEASE_URL;
                break;
            case ReleaseChannel.DEV:
                url = CONSTANTS.DEV_CHANNEL_RELEASE_URL;
                break;
            case ReleaseChannel.LAUNCHER:
                url = CONSTANTS.LAUNCHER_RELEASE_URL;
                break;
            default:
                url = CONSTANTS.MAIN_CHANNEL_RELEASE_URL;
                break;
        }

        return await Task.Run(async () =>
        {
            var d = await TryGetReleaseData(url);

            if (d != null)
                if (!ReleaseData.TryAdd(channel, d))
                    ReleaseData[channel] = d;

            return d;
        });
    }

    private static async Task<GitHubReleaseData?> TryGetReleaseData(string url)
    {
        HttpRequestMessage restApi = new HttpRequestMessage()
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri(url),
        };
        restApi.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        restApi.Headers.Add("User-Agent", "Public");

        try
        {
            var httpClient = new HttpClient();
            string jsonResponse = await httpClient.Send(restApi).Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GitHubReleaseData>(jsonResponse);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return null;
        }
    }

    /// <summary>
    /// Supports dev/main/branch channels, not launcher channel
    /// </summary>
    /// <param name="channel"></param>
    /// <param name="downloadProgress"></param>
    /// <param name="onCompleted"></param>
    /// <param name="parentWindow"></param>
    public static async void DownloadAndInstallZip(ReleaseChannel channel, DownloadProgress downloadProgress, Action onCompleted, Window? parentWindow = null)
    {
        if (!HaveData(channel)) return;

        if (Process.GetProcessesByName("TazUO").Length > 0)
        {
            if (parentWindow != null)
            {
                bool proceed = await Utility.ShowConfirmationDialog(
                    parentWindow,
                    "TazUO is Running",
                    "TazUO appears to be running. Updating while the client is running may cause issues.\n\nDo you want to proceed with the update anyway?"
                );

                if (!proceed)
                {
                    onCompleted();
                    return;
                }
            }
            else
            {
                onCompleted();
                return;
            }
        }

        GitHubReleaseData releaseData = ReleaseData[channel];

        if (releaseData == null || releaseData.assets == null)
        {
            _ = TryGetReleaseData(channel);
            return;
        }

        string extractTo = PlatformHelper.IsMac ? PathHelper.ClientAppMacOSPath : PathHelper.ClientPath;

        await Task.Run(() =>
        {
            GitHubReleaseData.Asset? selectedAsset = null;
            string platformZipName = PlatformHelper.GetPlatformZipName();

            // First, try to find platform-specific zip
            foreach (GitHubReleaseData.Asset asset in releaseData.assets)
            {
                if (asset.name != null && asset.name.EndsWith(platformZipName) && asset.browser_download_url != null)
                {
                    selectedAsset = asset;
                    break;
                }
            }

            // Fallback to current method if platform-specific zip not found
            if (selectedAsset == null)
            {
                foreach (GitHubReleaseData.Asset asset in releaseData.assets)
                {
                    if (asset.name != null && asset.name.EndsWith(".zip") && asset.name.StartsWith(CONSTANTS.ZIP_STARTS_WITH) && asset.browser_download_url != null)
                    {
                        selectedAsset = asset;
                        break;
                    }
                }
            }

            if (selectedAsset != null)
            {
                Console.WriteLine($"Picked for download: {selectedAsset.name} from {selectedAsset.browser_download_url}");
                try
                {
                    string tempFilePath = Path.GetTempFileName();
                    using (var file = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        HttpClient httpClient = new HttpClient();
                        httpClient.DownloadAsync(selectedAsset.browser_download_url, file, downloadProgress).Wait();
                    }

                    Directory.CreateDirectory(extractTo);
                    ZipFile.ExtractToDirectory(tempFilePath, extractTo, true);

                    if (PlatformHelper.IsMac)
                    {
                        CreateMacAppBundle();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            onCompleted?.Invoke();
        });
    }

    /// <summary>
    /// Creates the macOS .app bundle scaffolding around the extracted flat zip files.
    /// The flat zip is extracted to TazUO.app/Contents/MacOS/.
    /// This method creates Info.plist, PkgInfo, moves icon.icns to Resources/,
    /// copies v.txt to the ClientPath root, and ad-hoc codesigns the bundle.
    /// </summary>
    private static void CreateMacAppBundle()
    {
        string appBundle = PathHelper.ClientAppBundlePath;
        string contentsDir = Path.Combine(appBundle, "Contents");
        string macosDir = Path.Combine(contentsDir, "MacOS");
        string resourcesDir = Path.Combine(contentsDir, "Resources");

        Directory.CreateDirectory(resourcesDir);

        // Move icon.icns from MacOS/ to Resources/ (CI includes it in the flat zip)
        string iconSrc = Path.Combine(macosDir, "icon.icns");
        string iconDest = Path.Combine(resourcesDir, "icon.icns");
        if (File.Exists(iconSrc))
        {
            if (File.Exists(iconDest))
                File.Delete(iconDest);
            File.Move(iconSrc, iconDest);
        }

        // Copy v.txt to ClientPath root so version detection works
        string versionSrc = Path.Combine(macosDir, "v.txt");
        string versionDest = Path.Combine(PathHelper.ClientPath, "v.txt");
        if (File.Exists(versionSrc))
        {
            File.Copy(versionSrc, versionDest, true);
        }

        // Move data directories from MacOS/ to ClientPath root so the game
        // finds them via ExecutablePath (which resolves outside the .app)
        string[] dataDirs = { "LegionScripts", "Data", "ExternalImages" };
        foreach (string dirName in dataDirs)
        {
            string src = Path.Combine(macosDir, dirName);
            string dest = Path.Combine(PathHelper.ClientPath, dirName);
            if (Directory.Exists(src))
                MergeDirectory(src, dest);
        }

        // Read version for Info.plist
        string version = "1.0.0";
        if (File.Exists(versionDest))
        {
            try { version = File.ReadAllText(versionDest).Trim(); } catch { }
        }

        // Write Info.plist
        string plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
  <key>CFBundleExecutable</key>
  <string>{CONSTANTS.NATIVE_EXECUTABLE_NAME}</string>
  <key>CFBundleIdentifier</key>
  <string>com.crameep.tazuo</string>
  <key>CFBundleName</key>
  <string>TazUO</string>
  <key>CFBundleIconFile</key>
  <string>icon</string>
  <key>CFBundleShortVersionString</key>
  <string>{version}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSMinimumSystemVersion</key>
  <string>11.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>";
        File.WriteAllText(Path.Combine(contentsDir, "Info.plist"), plist);

        // Write PkgInfo
        File.WriteAllText(Path.Combine(contentsDir, "PkgInfo"), "APPL????");

        // Ad-hoc codesign
        try
        {
            using var codesign = new System.Diagnostics.Process();
            codesign.StartInfo.FileName = "codesign";
            codesign.StartInfo.Arguments = $"--force --deep --sign - \"{appBundle}\"";
            codesign.StartInfo.UseShellExecute = false;
            codesign.StartInfo.RedirectStandardError = true;
            codesign.Start();
            string stderr = codesign.StandardError.ReadToEnd();
            if (!codesign.WaitForExit(15000))
            {
                codesign.Kill();
                Console.WriteLine("Codesign timed out (non-fatal)");
            }
            else if (codesign.ExitCode != 0)
            {
                Console.WriteLine($"Codesign failed with exit code {codesign.ExitCode}: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Codesign not available (non-fatal): {ex.Message}");
        }

        Console.WriteLine($"Created macOS .app bundle at {appBundle}");
    }

    /// <summary>
    /// Recursively merges source directory into destination without overwriting existing files.
    /// Removes source files/dirs after merging.
    /// </summary>
    private static void MergeDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (string file in Directory.EnumerateFiles(src))
        {
            string destFile = Path.Combine(dest, Path.GetFileName(file));
            if (!File.Exists(destFile))
                File.Move(file, destFile);
            else
                File.Delete(file);
        }

        foreach (string subDir in Directory.EnumerateDirectories(src))
        {
            string dirName = Path.GetFileName(subDir);
            MergeDirectory(subDir, Path.Combine(dest, dirName));
        }

        try { Directory.Delete(src, false); } catch { }
    }
}