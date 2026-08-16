namespace TazUOLauncher;

public enum ReleaseChannel
{
    // These values are persisted as integers in the launcher save file: LauncherSettings uses
    // JsonSerializer without a JsonStringEnumConverter, so System.Text.Json writes the numeric
    // value. The names may be renamed freely, but the numbers must stay put or existing installs
    // will read back the wrong channel.
    INVALID = 0,

    /// <summary>Launcher menu "Stable". Published from the TazUO main branch.</summary>
    STABLE = 1,

    /// <summary>Launcher menu "Bleeding Edge". Published from the TazUO dev branch.</summary>
    BLEEDING_EDGE = 2,

    /// <summary>The launcher's own updates, not a client channel.</summary>
    LAUNCHER = 3,

    /// <summary>Launcher menu "Feature Branch". Any TazUO branch published as a branch-* prerelease.</summary>
    FEATURE_BRANCH = 4
}

public enum ClientStatus
{
    INITIALIZING,
    DOWNLOAD_IN_PROGRESS,
    NO_LOCAL_CLIENT,
    READY
}