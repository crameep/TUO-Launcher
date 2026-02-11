using System;
using System.Text.RegularExpressions;

namespace TazUOLauncher;

/// <summary>
/// Represents a client version string that can handle stable (v1.0.0),
/// dev (0.0.0-dev.YYYYMMDD.SHA), and branch (branch-NAME.YYYYMMDD.SHA) formats.
/// </summary>
internal class ClientVersionInfo
{
    public static readonly ClientVersionInfo Empty = new ClientVersionInfo(string.Empty);

    public string RawVersion { get; }
    public VersionKind Kind { get; }

    // Populated for Stable versions
    public Version? SemVer { get; }

    // Populated for Dev/Branch versions
    public string? DateComponent { get; }
    public string? ShaComponent { get; }
    public string? BranchName { get; } // Only for Branch kind

    private ClientVersionInfo(string raw)
    {
        RawVersion = raw?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(RawVersion))
        {
            Kind = VersionKind.Unknown;
            return;
        }

        // Try dev format: 0.0.0-dev.YYYYMMDD.SHA
        var devMatch = Regex.Match(RawVersion, @"^0\.0\.0-dev\.(\d{8})\.([a-f0-9]+)$");
        if (devMatch.Success)
        {
            Kind = VersionKind.Dev;
            DateComponent = devMatch.Groups[1].Value;
            ShaComponent = devMatch.Groups[2].Value;
            return;
        }

        // Try branch format: branch-NAME.YYYYMMDD.SHA
        var branchMatch = Regex.Match(RawVersion, @"^branch-(.+?)\.(\d{8})\.([a-f0-9]+)$");
        if (branchMatch.Success)
        {
            Kind = VersionKind.Branch;
            BranchName = branchMatch.Groups[1].Value;
            DateComponent = branchMatch.Groups[2].Value;
            ShaComponent = branchMatch.Groups[3].Value;
            return;
        }

        // Try stable format: v1.0.0 or 1.0.0
        string cleaned = RawVersion.StartsWith('v') ? RawVersion.Substring(1) : RawVersion;
        if (Version.TryParse(cleaned, out var semver))
        {
            Kind = VersionKind.Stable;
            SemVer = semver;
            return;
        }

        Kind = VersionKind.Unknown;
    }

    public static ClientVersionInfo Parse(string versionString)
    {
        return new ClientVersionInfo(versionString);
    }

    /// <summary>
    /// Determines if a remote version represents an available update over this (local) version.
    /// For stable: numeric semver comparison.
    /// For dev/branch: any difference in version string means update available.
    /// Cross-kind comparisons always indicate an update is available.
    /// </summary>
    public static bool IsUpdateAvailable(ClientVersionInfo local, ClientVersionInfo remote)
    {
        if (remote.Kind == VersionKind.Unknown)
            return false;

        if (local.Kind == VersionKind.Unknown)
            return true;

        // Cross-kind = always update (channel switch)
        if (local.Kind != remote.Kind)
            return true;

        switch (local.Kind)
        {
            case VersionKind.Stable:
                return remote.SemVer > local.SemVer;

            case VersionKind.Dev:
            case VersionKind.Branch:
                // Any difference means update available
                return !string.Equals(local.RawVersion, remote.RawVersion, StringComparison.Ordinal);

            default:
                return true;
        }
    }

    /// <summary>
    /// Returns a human-readable display string for this version.
    /// </summary>
    public string ToDisplayString()
    {
        if (Kind == VersionKind.Unknown || string.IsNullOrEmpty(RawVersion))
            return "Unknown";

        switch (Kind)
        {
            case VersionKind.Stable:
                return $"v{SemVer!.Major}.{SemVer.Minor}.{SemVer.Build}";
            case VersionKind.Dev:
                return $"dev.{DateComponent}.{ShaComponent}";
            case VersionKind.Branch:
                return $"{BranchName}.{DateComponent}.{ShaComponent}";
            default:
                return RawVersion;
        }
    }

    public override string ToString() => RawVersion;

    public enum VersionKind
    {
        Unknown,
        Stable,
        Dev,
        Branch
    }
}
