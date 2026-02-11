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

        string extractTo = PathHelper.ClientPath;

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
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            
            onCompleted?.Invoke();
        });
    }
}