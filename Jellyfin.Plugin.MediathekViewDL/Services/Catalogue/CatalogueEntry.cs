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

    /// <summary>Gets or sets the last upstream observation.</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Gets or sets the start of confirmed unavailability.</summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>Gets or sets the last independently counted failure.</summary>
    public DateTimeOffset? LastFailure { get; set; }

    /// <summary>Gets or sets the count of definitive unavailable responses.</summary>
    public int MissingChecks { get; set; }
}
