using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Durable catalogue checkpoints and ownership journal.</summary>
public sealed class CatalogueState
{
    /// <summary>Gets or sets the managed root.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the last successful discovery time.</summary>
    public DateTimeOffset? LastSync { get; set; }

    /// <summary>Gets or sets the last successful full discovery time.</summary>
    public DateTimeOffset? LastFullSync { get; set; }

    /// <summary>Gets the owned entries.</summary>
    public Dictionary<string, CatalogueEntry> Entries { get; init; } = new(StringComparer.Ordinal);
}
