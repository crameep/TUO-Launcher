using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace TazUOLauncher;

internal static class Utility
{
    public static ClientVersionInfo GetClientVersionInfo(this GitHubReleaseData data)
    {
        if (data == null)
            return ClientVersionInfo.Empty;

        // Try tag_name first (most reliable for stable releases like "v1.0.0")
        if (!string.IsNullOrEmpty(data.tag_name))
        {
            var parsed = ClientVersionInfo.Parse(data.tag_name);
            if (parsed.Kind != ClientVersionInfo.VersionKind.Unknown)
                return parsed;
        }

        // For bleeding-edge releases (tag = "TazUO-BleedingEdge"), read the version
        // string directly from the release body (matches v.txt written by CI)
        if (data.tag_name == "TazUO-BleedingEdge" && data.body != null)
        {
            // Preferred: exact version embedded in release body by CI
            var versionMatch = Regex.Match(data.body, @"Version:\s*(.+)");
            if (versionMatch.Success)
            {
                var parsed = ClientVersionInfo.Parse(versionMatch.Groups[1].Value.Trim());
                if (parsed.Kind != ClientVersionInfo.VersionKind.Unknown)
                    return parsed;
            }

            // Legacy fallback: construct from published_at + commit SHA
            var shaMatch = Regex.Match(data.body, @"Commit:\s*([a-f0-9]+)");
            if (shaMatch.Success)
            {
                string sha = shaMatch.Groups[1].Value;
                if (sha.Length > 7)
                    sha = sha.Substring(0, 7);
                string date = data.published_at.ToUniversalTime().ToString("yyyyMMdd");
                return ClientVersionInfo.Parse($"0.0.0-dev.{date}.{sha}");
            }
        }

        // Try release name as fallback
        if (!string.IsNullOrEmpty(data.name))
        {
            var parsed = ClientVersionInfo.Parse(data.name);
            if (parsed.Kind != ClientVersionInfo.VersionKind.Unknown)
                return parsed;
        }

        return ClientVersionInfo.Empty;
    }

    /// <summary>
    /// Extracts a System.Version from the release data (for launcher self-update only).
    /// The launcher always uses semver from its assembly metadata.
    /// </summary>
    public static Version GetLauncherSemVer(this GitHubReleaseData data)
    {
        if (data != null && data.name != null)
        {
            string name = data.name;
            if (name.StartsWith('v'))
                name = name.Substring(1);
            else
            {
                var m = Regex.Match(name, @"v(\d+\.\d+\.\d+)");
                if (m.Success)
                    name = m.Groups[1].Value;
            }

            if (Version.TryParse(name, out var version))
                return version;
        }
        return new Version(0, 0, 0);
    }

    public static async Task DownloadAsync(this HttpClient client, string requestUri, Stream destination, IProgress<float> progress, CancellationToken cancellationToken = default)
    {
        // Get the http headers first to examine the content length
        using (var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead))
        {
            var contentLength = response.Content.Headers.ContentLength;

            using (var download = await response.Content.ReadAsStreamAsync())
            {

                // Ignore progress reporting when no progress reporter was 
                // passed or when the content length is unknown
                if (progress == null || !contentLength.HasValue)
                {
                    await download.CopyToAsync(destination);
                    return;
                }

                // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
                var relativeProgress = new Progress<long>(totalBytes => progress.Report((float)totalBytes / contentLength.Value));
                // Use extension method to report progress while downloading
                await download.CopyToAsync(destination, 81920, relativeProgress, cancellationToken);
                progress.Report(1);
            }
        }
    }

    public static async Task CopyToAsync(this Stream source, Stream destination, int bufferSize, IProgress<long> progress, CancellationToken cancellationToken = default)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (!source.CanRead)
            throw new ArgumentException("Has to be readable", nameof(source));
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (!destination.CanWrite)
            throw new ArgumentException("Has to be writable", nameof(destination));
        if (bufferSize < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        var buffer = new byte[bufferSize];
        long totalBytesRead = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;
            progress?.Report(totalBytesRead);
        }
    }

    public static void LaunchClient(Profile profile, Window window, bool force = false)
    {
        string path = PathHelper.ClientExecutablePath();
        string clientPath = Path.Combine(profile.CUOSettings.UltimaOnlineDirectory, "client.exe");

        if (!File.Exists(path)) return;
        
        if (!force && ClientVersionHelper.TryParseFromFile(clientPath, out var version) && version != profile.CUOSettings.ClientVersion)
        {
            _ = ShowConfirmationDialog(
                window, 
                "Client version mismatch", 
                "The client version in the launcher settings does not match the client version on your system.\n\nDo you want to update the client version in the launcher settings to match the client version on file?",
                (b) =>
                {
                    if (b)
                    {
                        profile.CUOSettings.ClientVersion = version;
                        profile.Save();
                    }
                    
                    LaunchClient(profile, window, true);
                }
            );

            return;
        }

        try
        {
            string clientArgs = $"-settings \"{profile.GetSettingsFilePath()}\" -skipupdatecheck";
            if (profile.CUOSettings.AutoLogin && !string.IsNullOrEmpty(profile.LastCharacterName))
            {
                clientArgs += $" -lastcharactername \"{profile.LastCharacterName}\"";
            }

            if (profile.CUOSettings.AutoLogin)
            {
                clientArgs += " -skiploginscreen";
            }

            if (!string.IsNullOrEmpty(profile.AdditionalArgs))
            {
                clientArgs += " " + profile.AdditionalArgs;
            }

            ProcessStartInfo proc;
            if (PlatformHelper.IsMac && Directory.Exists(PathHelper.ClientAppBundlePath))
            {
                // Launch via 'open' for proper macOS .app behavior (icon, no terminal)
                proc = new ProcessStartInfo("open", $"-a \"{PathHelper.ClientAppBundlePath}\" --args {clientArgs}");
                proc.WorkingDirectory = PathHelper.ClientPath;
            }
            else
            {
                proc = new ProcessStartInfo(path, clientArgs);
                proc.WorkingDirectory = PathHelper.ClientPath;
            }

            Process.Start(proc);
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine(ex.ToString());
            if (ex.Message.Contains("Permission denied"))
            {
                ((MainWindowViewModel)MainWindow.Instance.DataContext).DangerNoticeString =
                    "Permission denied when trying to launch TazUO";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }

    public static bool TryImportCUOProfiles()
    {
        string CUOPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassicUOLauncher", "launcher_settings.xml");
        if (!File.Exists(CUOPath)) return false;

        try
        {
            XmlDocument cuoLauncher = new XmlDocument();
            cuoLauncher.Load(CUOPath);

            XmlNode? root = cuoLauncher.DocumentElement;

            if (root == null) return false;

            XmlNode? profiles = root["profiles"];
            if (profiles == null) return false;

            foreach (XmlNode profile in profiles.ChildNodes)
            {
                Profile newProfile = new Profile();

                if (profile.Name != "profile" || profile.Attributes == null) break;

                foreach (XmlAttribute attr in profile.Attributes)
                {
                    if (attr.Name != null && attr.Value != null)
                        switch (attr.Name)
                        {
                            case "name":
                                newProfile.Name = attr.Value;
                                break;
                            case "username":
                                newProfile.CUOSettings.Username = attr.Value;
                                break;
                            case "password":
                                newProfile.CUOSettings.Password = attr.Value;
                                break;
                            case "server":
                                newProfile.CUOSettings.IP = attr.Value;
                                break;
                            case "port":
                                if (ushort.TryParse(attr.Value, out ushort port))
                                {
                                    newProfile.CUOSettings.Port = port;
                                }
                                break;
                            case "charname":
                                newProfile.LastCharacterName = attr.Value;
                                break;
                            case "client_version":
                                newProfile.CUOSettings.ClientVersion = attr.Value;
                                break;
                            case "uopath":
                                newProfile.CUOSettings.UltimaOnlineDirectory = attr.Value;
                                break;
                            case "last_server_index":
                                if (ushort.TryParse(attr.Value, out ushort lserver))
                                {
                                    newProfile.CUOSettings.LastServerNum = lserver;

                                }
                                break;
                            case "last_server_name":
                                newProfile.CUOSettings.LastServerName = attr.Value;
                                break;
                            case "save_account":
                                if (bool.TryParse(attr.Value, out bool sacount))
                                {
                                    newProfile.CUOSettings.SaveAccount = sacount;
                                }
                                break;
                            case "autologin":
                                if (bool.TryParse(attr.Value, out bool autolog))
                                {
                                    newProfile.CUOSettings.AutoLogin = autolog;
                                }
                                break;
                            case "reconnect":
                                if (bool.TryParse(attr.Value, out bool recon))
                                {
                                    newProfile.CUOSettings.Reconnect = recon;
                                }
                                break;
                            case "reconnect_time":
                                if (int.TryParse(attr.Value, out int n))
                                {
                                    newProfile.CUOSettings.ReconnectTime = n;
                                }
                                break;
                            case "has_music":
                                if (bool.TryParse(attr.Value, out bool nn))
                                {
                                    newProfile.CUOSettings.LoginMusic = nn;
                                }
                                break;
                            case "use_verdata":
                                if (bool.TryParse(attr.Value, out bool nnn))
                                {
                                    newProfile.CUOSettings.UseVerdata = nnn;
                                }
                                break;
                            case "music_volume":
                                if (int.TryParse(attr.Value, out int nnnn))
                                {
                                    newProfile.CUOSettings.LoginMusicVolume = nnnn;
                                }
                                break;
                            case "encryption_type":
                                if (byte.TryParse(attr.Value, out byte nnnnn))
                                {
                                    newProfile.CUOSettings.Encryption = nnnnn;
                                }
                                break;
                            case "force_driver":
                                if (byte.TryParse(attr.Value, out byte nnnnnn))
                                {
                                    newProfile.CUOSettings.ForceDriver = nnnnnn;
                                }
                                break;
                            case "args":
                                newProfile.AdditionalArgs = attr.Value;
                                break;
                        }
                }
                newProfile.Save();
            }
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return false;
    }

    public static async Task<string> OpenFolderDialog(Window parent, string title)
    {
        var topLevel = TopLevel.GetTopLevel(parent);
        if (topLevel == null) return string.Empty;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        if (folders.Count >= 1)
        {
            Console.WriteLine($"User selected folder: {folders[0].Path.LocalPath}");
            return folders[0].Path.LocalPath;
        }
        else
        {
            Console.WriteLine("Looks like no folder was selected.");
        }
        return string.Empty;
    }

    public static async Task<string> OpenFileDialog(Window parent, string title)
    {
        var topLevel = TopLevel.GetTopLevel(parent);
        if (topLevel == null) return string.Empty;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (files.Count >= 1)
        {
            Console.WriteLine($"User selected folder: {files[0].Path.LocalPath}");
            return files[0].Path.LocalPath;
        }
        else
        {
            Console.WriteLine("Looks like no file was selected.");
        }
        return string.Empty;
    }

    public static async Task<bool> ShowConfirmationDialog(Window parent, string title, string message, Action<bool> onResult = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 20
        };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Spacing = 10
        };

        bool result = false;

        var yesButton = new Button
        {
            Content = "Yes",
            MinWidth = 80,
            Padding = new Avalonia.Thickness(10, 5)
        };
        yesButton.Click += (s, e) =>
        {
            result = true;
            dialog.Close();
        };

        var noButton = new Button
        {
            Content = "No",
            MinWidth = 80,
            Padding = new Avalonia.Thickness(10, 5)
        };
        noButton.Click += (s, e) =>
        {
            result = false;
            dialog.Close();
        };

        buttonPanel.Children.Add(yesButton);
        buttonPanel.Children.Add(noButton);

        panel.Children.Add(messageText);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        await dialog.ShowDialog(parent);
        onResult?.Invoke(result);
        return result;
    }
}