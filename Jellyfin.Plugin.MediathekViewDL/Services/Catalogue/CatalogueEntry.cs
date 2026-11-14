using System;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Provenance, hashes and availability history for one owned link.</summary>
public sealed class CatalogueEntry
{
    /// <summary>Gets or sets the path relative to the managed root.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the last selected stream URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected link hash.</summary>
    public string LinkHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected metadata hash.</summary>
    public string NfoHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the last metadata guard applied to the Jellyfin item.</summary>
    public int MetadataGuardVersion { get; set; }

    /// <summary>Gets or sets the original upstream identity for diagnostics.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the unabridged broadcaster title.</summary>
    public string SourceTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the original programme topic.</summary>
    public string SourceTopic { get; set; } = string.Empty;

    /// <summary>Gets or sets plot evidence for matching later broadcaster aliases.</summary>
    public string SourceDescription { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalized movie duplicate identity.</summary>
    public string? DuplicateKey { get; set; }

    /// <summary>Gets or sets the last upstream observation.</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Gets or sets the start of confirmed unavailability.</summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>Gets or sets the last independently counted failure.</summary>
    public DateTimeOffset? LastFailure { get; set; }

    /// <summary>Gets or sets the count of definitive unavailable responses.</summary>
    public int MissingChecks { get; set; }
}
