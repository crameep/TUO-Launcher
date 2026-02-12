using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TazUOLauncher;

public partial class MainWindow : Window
{
    public static Window Instance { get; private set; }
    private MainWindowViewModel viewModel;
    private ClientStatus clientStatus = ClientStatus.INITIALIZING;
    private ReleaseChannel nextDownloadType = ReleaseChannel.INVALID;
    private ProfileEditorWindow? profileWindow;
    private Profile? selectedProfile;
    private bool launcherUpdateFailed;
    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        DataContext = viewModel = new MainWindowViewModel();

        viewModel.MainChannelSelected = LauncherSettings.GetLauncherSaveFile.DownloadChannel == ReleaseChannel.MAIN;
        viewModel.DevChannelSelected = LauncherSettings.GetLauncherSaveFile.DownloadChannel == ReleaseChannel.DEV;
        viewModel.BranchChannelSelected = LauncherSettings.GetLauncherSaveFile.DownloadChannel == ReleaseChannel.BRANCH;

        if (viewModel.BranchChannelSelected)
            _ = PopulateBranchNames();

        DoChecksAsync();
        LoadProfiles();

        Timer periodicChecks = new Timer(TimeSpan.FromHours(1));
        periodicChecks.AutoReset = true;
        periodicChecks.Elapsed += (sender, args) => DoChecksAsync();
        periodicChecks.Start();
        
        DateTime dt = DateTime.Now;
        if(dt.Month == 12)
            MainCanvas.Children.Add(new SnowOverlayControl(new Rect(0, 0, 800, 450)));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        profileWindow?.Close();

        LauncherSettings.GetLauncherSaveFile.Save().ConfigureAwait(false);

        base.OnClosing(e);
    }
    private async void LoadProfiles()
    {
        await ProfileManager.GetAllProfiles();
        SetProfileSelectorComboBox();
    }
    private async void DoChecksAsync()
    {
        var remoteVersionInfo = UpdateHelper.GetAllReleaseData();
        ClientExistsChecks(); //Doesn't need to wait for release data

        await remoteVersionInfo; //Things after this are waiting for release data
        UpdateVersionStrings();
        CheckLauncherVersion();
        ClientUpdateChecks();
        if (!await AutoUpdateHandler())
            HandleUpdates();
    }
    private void SetProfileSelectorComboBox()
    {
        viewModel.Profiles = [CONSTANTS.EDIT_PROFILES, .. ProfileManager.GetProfileNames()];

        int i = 0;
        foreach (var s in viewModel.Profiles)
        {
            if (s == LauncherSettings.GetLauncherSaveFile.LastSelectedProfileName)
            {
                ProfileSelector.SelectedIndex = i;
                break;
            }
            i++;
        }
    }
    private void CheckLauncherVersion()
    {
        if (!UpdateHelper.HaveData(ReleaseChannel.LAUNCHER)) return;

        var data = UpdateHelper.ReleaseData[ReleaseChannel.LAUNCHER];
        if (data.GetLauncherSemVer() > LauncherVersion.GetLauncherVersion())
        {
            viewModel.DangerNoticeString = $"A launcher update is available! ({LauncherVersion.GetLauncherVersion().ToHumanReable()} -> {data.GetLauncherSemVer().ToHumanReable()})";
            viewModel.ShowLauncherUpdateButton = true;
        }
    }
    private void UpdateVersionStrings()
    {
        var channel = LauncherSettings.GetLauncherSaveFile.DownloadChannel;
        if (UpdateHelper.HaveData(channel))
        {
            var versionInfo = UpdateHelper.ReleaseData[channel].GetClientVersionInfo();
            if (channel == ReleaseChannel.BRANCH && !string.IsNullOrEmpty(LauncherSettings.GetLauncherSaveFile.SelectedBranch))
                viewModel.RemoteVersionString = $"Branch: {versionInfo.ToDisplayString()}";
            else
                viewModel.RemoteVersionString = string.Format(CONSTANTS.REMOTE_VERSION_FORMAT, versionInfo.ToDisplayString());
        }
    }
    private void ClientExistsChecks()
    {
        if (!ClientHelper.ExecutableExists())
        {
            viewModel.LocalVersionString = string.Format(CONSTANTS.LOCAL_VERSION_FORMAT, "N/A");
            clientStatus = ClientStatus.NO_LOCAL_CLIENT;
            nextDownloadType = LauncherSettings.GetLauncherSaveFile.DownloadChannel;
        }
        else
        {
            viewModel.DangerNoticeString = string.Empty;
            viewModel.LocalVersionString = string.Format(CONSTANTS.LOCAL_VERSION_FORMAT, ClientHelper.LocalClientVersion.ToDisplayString());
            viewModel.PlayButtonEnabled = true;
            clientStatus = ClientStatus.READY;
        }
    }
    private void ClientUpdateChecks()
    {
        if (clientStatus > ClientStatus.NO_LOCAL_CLIENT) //Only check for updates if we have a client installed already
            if (UpdateHelper.HaveData(LauncherSettings.GetLauncherSaveFile.DownloadChannel))
            {
                var remoteVersion = UpdateHelper.ReleaseData[LauncherSettings.GetLauncherSaveFile.DownloadChannel].GetClientVersionInfo();
                if (ClientVersionInfo.IsUpdateAvailable(ClientHelper.LocalClientVersion, remoteVersion))
                {
                    nextDownloadType = LauncherSettings.GetLauncherSaveFile.DownloadChannel;
                }
            }
    }
    private void HandleUpdates()
    {
        if (nextDownloadType != ReleaseChannel.INVALID)
        {
            switch (nextDownloadType)
            {
                case ReleaseChannel.MAIN or ReleaseChannel.DEV or ReleaseChannel.BRANCH:
                    viewModel.UpdateButtonString = clientStatus == ClientStatus.NO_LOCAL_CLIENT ? CONSTANTS.NO_CLIENT_AVAILABLE : CONSTANTS.CLIENT_UPDATE_AVAILABLE;
                    viewModel.ShowDownloadAvailableButton = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Must be called after ClientUpdateChecks
    /// </summary>
    private async Task<bool> AutoUpdateHandler()
    {
        if (nextDownloadType == ReleaseChannel.INVALID) return false;

        if (clientStatus <= ClientStatus.DOWNLOAD_IN_PROGRESS) return false;

        if (Process.GetProcessesByName("TazUO").Length > 0)
        {
            bool proceed = await Utility.ShowConfirmationDialog(
                this,
                "TazUO is Running",
                "TazUO appears to be running. Updating while the client is running may cause issues.\n\nDo you want to proceed with the update anyway?"
            );

            if (!proceed) return false;
        }

        if (nextDownloadType > ReleaseChannel.INVALID && LauncherSettings.GetLauncherSaveFile.AutoDownloadUpdates)
        {
            DoNextDownload();
            return true;
        }

        return false;
    }
    private void DoNextDownload()
    {
        if (nextDownloadType == ReleaseChannel.INVALID || clientStatus == ClientStatus.DOWNLOAD_IN_PROGRESS) return;

        viewModel.ShowDownloadAvailableButton = false;
        var prog = new DownloadProgress();
        prog.DownloadProgressChanged += (_, _) =>
        {
            Dispatcher.UIThread.InvokeAsync(() => viewModel.DownloadProgressBarPercent = (int)(prog.ProgressPercentage * 100));
        };

        viewModel.PlayButtonEnabled = false;
        clientStatus = ClientStatus.DOWNLOAD_IN_PROGRESS;
        viewModel.ShowDownloadAvailableButton = false;
        viewModel.DownloadProgressBarPercent = 0;
        viewModel.ShowDownloadProgressBar = true;

        UpdateHelper.DownloadAndInstallZip(nextDownloadType, prog, () =>
        {
            viewModel.ShowDownloadProgressBar = false;
            nextDownloadType = ReleaseChannel.INVALID;
            ClientHelper.LocalClientVersion = ClientHelper.LocalClientVersion; //Client version is re-checked when setting this var
            ClientExistsChecks();
            ClientUpdateChecks();
            HandleUpdates();
        }, this);
    }
    private void OpenEditProfiles()
    {
        if (profileWindow != null)
        {
            profileWindow.Show();
            return;
        }
        profileWindow = new ProfileEditorWindow();
        profileWindow.Show();
        profileWindow.Closed += (s, e) =>
        {
            profileWindow = null;
            LoadProfiles();
        };
    }

    public void SetStableChannelClicked(object sender, RoutedEventArgs args)
    {
        if (LauncherSettings.GetLauncherSaveFile.DownloadChannel == ReleaseChannel.MAIN) return;

        viewModel.MainChannelSelected = true;
        viewModel.DevChannelSelected = false;
        viewModel.BranchChannelSelected = false;
        LauncherSettings.GetLauncherSaveFile.DownloadChannel = ReleaseChannel.MAIN;
        RecheckAfterChannelUpdated();
    }
    public void SetDevChannelClicked(object sender, RoutedEventArgs args)
    {
        if (LauncherSettings.GetLauncherSaveFile.DownloadChannel == ReleaseChannel.DEV) return;

        viewModel.DevChannelSelected = true;
        viewModel.MainChannelSelected = false;
        viewModel.BranchChannelSelected = false;
        LauncherSettings.GetLauncherSaveFile.DownloadChannel = ReleaseChannel.DEV;
        RecheckAfterChannelUpdated();
    }
    public async void SetBranchChannelClicked(object sender, RoutedEventArgs args)
    {
        if (LauncherSettings.GetLauncherSaveFile.DownloadChannel == ReleaseChannel.BRANCH) return;

        viewModel.BranchChannelSelected = true;
        viewModel.MainChannelSelected = false;
        viewModel.DevChannelSelected = false;
        LauncherSettings.GetLauncherSaveFile.DownloadChannel = ReleaseChannel.BRANCH;

        await PopulateBranchNames();

        if (viewModel.BranchNames.Count == 0)
        {
            viewModel.DangerNoticeString = "No feature branch builds available.";
        }
        else if (string.IsNullOrEmpty(LauncherSettings.GetLauncherSaveFile.SelectedBranch))
        {
            viewModel.DangerNoticeString = "Select a branch from the menu.";
        }
        else
        {
            await SelectBranch(LauncherSettings.GetLauncherSaveFile.SelectedBranch);
        }
    }
    private async Task PopulateBranchNames()
    {
        var names = await UpdateHelper.GetBranchNames();
        viewModel.BranchNames.Clear();

        foreach (var item in BranchSubmenu.Items)
            if (item is MenuItem mi)
                mi.Click -= BranchMenuItemClick;

        BranchSubmenu.Items.Clear();
        foreach (var name in names)
        {
            viewModel.BranchNames.Add(name);
            var item = new MenuItem { Header = name, Tag = name };
            item.Click += BranchMenuItemClick;
            BranchSubmenu.Items.Add(item);
        }

        if (names.Count == 0)
        {
            BranchSubmenu.Items.Add(new MenuItem { Header = "No branch builds available", IsEnabled = false });
        }
    }
    private async void BranchMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string branchName)
            await SelectBranch(branchName);
    }
    private async Task SelectBranch(string branchName)
    {
        viewModel.SelectedBranchName = branchName;

        var branchRelease = await UpdateHelper.GetBranchReleaseData(branchName);
        if (branchRelease != null)
        {
            LauncherSettings.GetLauncherSaveFile.SelectedBranch = branchName;

            if (!UpdateHelper.ReleaseData.TryAdd(ReleaseChannel.BRANCH, branchRelease))
                UpdateHelper.ReleaseData[ReleaseChannel.BRANCH] = branchRelease;

            viewModel.DangerNoticeString = string.Empty;
            RecheckAfterChannelUpdated();
        }
        else
        {
            viewModel.DangerNoticeString = $"Branch '{branchName}' build not found. Select another branch.";
        }
    }

    private async void RecheckAfterChannelUpdated()
    {
        ClientHelper.CleanUpClientFiles(); //Clean up files before redownloading to avoid errors
        
        ClientHelper.LocalClientVersion = ClientHelper.LocalClientVersion; //Client version is re-checked when setting this var
        ClientExistsChecks();
        UpdateVersionStrings();
        ClientUpdateChecks();
        if(!await AutoUpdateHandler())
            HandleUpdates();
    }
    public void PlayButtonClicked(object sender, RoutedEventArgs args)
    {
        ClientHelper.TrySetPlusXUnix();
        if (selectedProfile != null)
            Utility.LaunchClient(selectedProfile, this);
    }
    public void DownloadButtonClicked(object sender, RoutedEventArgs args)
    {
        DoNextDownload();
    }
    public void ProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var dd = ((ComboBox)sender);
        if (dd == null) return;

        if (dd.SelectedIndex == 0)
        { //Edit Profile
            OpenEditProfiles();
            dd.SelectedIndex = -1;
        }
        else if (dd.SelectedItem != null && dd.SelectedItem is string si)
        {
            if (si != null)
                if (ProfileManager.TryFindProfile(si, out selectedProfile) && selectedProfile != null)
                    LauncherSettings.GetLauncherSaveFile.LastSelectedProfileName = selectedProfile.Name;
        }
    }
    public async void UpdateLauncherClicked(object sender, RoutedEventArgs args)
    {
        // After a failed self-update, fall back to opening the browser
        if (launcherUpdateFailed)
        {
            GoToLauncherDownload(sender, args);
            return;
        }

        viewModel.ShowLauncherUpdateButton = false;
        viewModel.DangerNoticeString = "Updating launcher...";
        viewModel.DownloadProgressBarPercent = 0;
        viewModel.ShowDownloadProgressBar = true;

        var prog = new DownloadProgress();
        prog.DownloadProgressChanged += (_, _) =>
        {
            Dispatcher.UIThread.InvokeAsync(() => viewModel.DownloadProgressBarPercent = (int)(prog.ProgressPercentage * 100));
        };

        bool success = await Task.Run(() => LauncherSelfUpdater.DownloadAndApplyUpdate(prog));

        if (success)
        {
            // New process was launched by the updater — exit gracefully
            Close();
            return;
        }

        // Update failed — offer manual download fallback
        viewModel.ShowDownloadProgressBar = false;
        string errorDetail = LauncherSelfUpdater.LastError ?? "Unknown error";
        viewModel.DangerNoticeString = $"Launcher update failed: {errorDetail}. Click to download manually.";
        viewModel.ShowLauncherUpdateButton = true;
        launcherUpdateFailed = true;
    }
    public void GoToLauncherDownload(object sender, RoutedEventArgs args)
    {
        if (UpdateHelper.HaveData(ReleaseChannel.LAUNCHER))
            WebLinks.OpenURLInBrowser(UpdateHelper.ReleaseData[ReleaseChannel.LAUNCHER].html_url ?? CONSTANTS.LAUNCHER_LATEST_URL);
        else
            WebLinks.OpenURLInBrowser(CONSTANTS.LAUNCHER_LATEST_URL);
    }
    public void EditProfilesClicked(object sender, RoutedEventArgs args)
    {
        OpenEditProfiles();
    }
    public void OpenWikiClicked(object sender, RoutedEventArgs args)
    {
        WebLinks.OpenURLInBrowser(CONSTANTS.WIKI_URL);
    }
    public void OpenDiscordClicked(object sender, RoutedEventArgs args)
    {
        WebLinks.OpenURLInBrowser(CONSTANTS.DISCORD_URL);
    }
    public void OpenGithubClicked(object sender, RoutedEventArgs args)
    {
        WebLinks.OpenURLInBrowser(CONSTANTS.GITHUB_URL);
    }
    public void DownloadMainBuildClick(object sender, RoutedEventArgs args)
    {
        if (clientStatus == ClientStatus.DOWNLOAD_IN_PROGRESS) return;
        nextDownloadType = ReleaseChannel.MAIN;
        DoNextDownload();
    }
    public void DownloadDevBuildClick(object sender, RoutedEventArgs args)
    {
        if (clientStatus == ClientStatus.DOWNLOAD_IN_PROGRESS) return;
        nextDownloadType = ReleaseChannel.DEV;
        DoNextDownload();
    }
    public void ImportCUOLauncherClick(object sender, RoutedEventArgs args)
    {
        if (!Utility.TryImportCUOProfiles())
        {
            viewModel.DangerNoticeString = "Failed to import CUO profiles, or no profiles found.";
            return;
        }
        LoadProfiles();
    }
    public void AutoInstallUpdatesClicked(object sender, RoutedEventArgs args)
    {
        LauncherSettings.GetLauncherSaveFile.AutoDownloadUpdates = viewModel.AutoApplyUpdates = !LauncherSettings.GetLauncherSaveFile.AutoDownloadUpdates;
    }
    public void ToolsButtonClick(object sender, RoutedEventArgs args)
    {
        ((Button)sender)?.ContextMenu?.Open();
        args.Handled = true;
    }
}


public class MainWindowViewModel : INotifyPropertyChanged
{
    private ObservableCollection<string> profiles = new ObservableCollection<string>();
    private bool showDownloadProgressBar;
    private int downloadProgressBarPercent;
    private bool showDownloadAvailableButton;
    private string remoteVersionString = string.Format(CONSTANTS.REMOTE_VERSION_FORMAT, "Checking...");
    private string localVersionString = "Local Version: Checking...";
    private string localLauncherVersionString = $"Launcher Version: {LauncherVersion.GetLauncherVersion().ToHumanReable()}";
    private string dangerNoticeString = string.Empty;
    private bool playButtonEnabled;
    private string updateButtonString = string.Empty;
    private bool showLauncherUpdateButton;
    private bool devChannelSelected;
    private bool mainChannelSelected;
    private bool branchChannelSelected;
    private bool dangerNoticeStringShowing;
    private bool autoApplyUpdates = LauncherSettings.GetLauncherSaveFile.AutoDownloadUpdates;
    private ObservableCollection<string> branchNames = new ObservableCollection<string>();
    private string selectedBranchName = LauncherSettings.GetLauncherSaveFile.SelectedBranch;

    public ObservableCollection<string> Profiles
    {
        get => profiles;
        set
        {
            profiles = value;
            OnPropertyChanged(nameof(Profiles));
        }
    }
    public bool ShowDownloadProgressBar
    {
        get => showDownloadProgressBar;
        set
        {
            showDownloadProgressBar = value;
            OnPropertyChanged(nameof(ShowDownloadProgressBar));
        }
    }
    public int DownloadProgressBarPercent
    {
        get => downloadProgressBarPercent;
        set
        {
            downloadProgressBarPercent = value;
            if (downloadProgressBarPercent > 100)
                downloadProgressBarPercent = 100;
            if (downloadProgressBarPercent < 0)
                downloadProgressBarPercent = 0;
            OnPropertyChanged(nameof(DownloadProgressBarPercent));
        }
    }
    public bool AutoApplyUpdates
    {
        get => autoApplyUpdates; set
        {
            autoApplyUpdates = value;
            OnPropertyChanged(nameof(AutoApplyUpdates));
        }
    }
    public bool DevChannelSelected
    {
        get => devChannelSelected; set
        {
            devChannelSelected = value;
            OnPropertyChanged(nameof(DevChannelSelected));
        }
    }
    public bool MainChannelSelected
    {
        get => mainChannelSelected; set
        {
            mainChannelSelected = value;
            OnPropertyChanged(nameof(MainChannelSelected));
        }
    }
    public bool BranchChannelSelected
    {
        get => branchChannelSelected; set
        {
            branchChannelSelected = value;
            OnPropertyChanged(nameof(BranchChannelSelected));
        }
    }
    public ObservableCollection<string> BranchNames
    {
        get => branchNames; set
        {
            branchNames = value;
            OnPropertyChanged(nameof(BranchNames));
        }
    }
    public string SelectedBranchName
    {
        get => selectedBranchName; set
        {
            selectedBranchName = value;
            OnPropertyChanged(nameof(SelectedBranchName));
        }
    }
    public bool ShowDownloadAvailableButton
    {
        get => showDownloadAvailableButton;
        set
        {
            showDownloadAvailableButton = value;
            OnPropertyChanged(nameof(ShowDownloadAvailableButton));
        }
    }
    public string RemoteVersionString
    {
        get => remoteVersionString; set
        {
            remoteVersionString = value;
            OnPropertyChanged(nameof(RemoteVersionString));
        }
    }
    public string LocalVersionString
    {
        get => localVersionString; set
        {
            localVersionString = value;
            OnPropertyChanged(nameof(LocalVersionString));
        }
    }
    public string LocalLauncherVersionString
    {
        get => localLauncherVersionString; set
        {
            localLauncherVersionString = value;
            OnPropertyChanged(nameof(LocalLauncherVersionString));
        }
    }
    public string DangerNoticeString
    {
        get => dangerNoticeString; set
        {
            dangerNoticeString = value;
            DangerNoticeStringShowing = !string.IsNullOrEmpty(value);
            OnPropertyChanged(nameof(DangerNoticeString));
        }
    }
    public bool DangerNoticeStringShowing
    {
        get => dangerNoticeStringShowing; set
        {
            dangerNoticeStringShowing = value;
            OnPropertyChanged(nameof(DangerNoticeStringShowing));
        }
    }
    public bool PlayButtonEnabled
    {
        get => playButtonEnabled; set
        {
            playButtonEnabled = value;
            OnPropertyChanged(nameof(PlayButtonEnabled));
        }
    }
    public string UpdateButtonString
    {
        get => updateButtonString; set
        {
            updateButtonString = value;
            OnPropertyChanged(nameof(UpdateButtonString));
        }
    }
    public bool ShowLauncherUpdateButton
    {
        get => showLauncherUpdateButton; set
        {
            showLauncherUpdateButton = value;
            OnPropertyChanged(nameof(ShowLauncherUpdateButton));
        }
    }
    public MainWindowViewModel()
    {
        Profiles = new ObservableCollection<string>() { CONSTANTS.EDIT_PROFILES };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}