using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Durable catalogue checkpoints and ownership journal.</summary>
public sealed class CatalogueState
{
    /// <summary>Gets or sets the generation used for crash-safe journal compaction.</summary>
    public Guid JournalGeneration { get; set; }

    /// <summary>Gets or sets the last completed source-title metadata migration.</summary>
    public int MetadataPolicyVersion { get; set; }

    /// <summary>Gets known contextual TV-film programme names, preserving future matching decisions.</summary>
    public HashSet<string> ContextualTopics { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the managed root.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the last successful discovery time.</summary>
    public DateTimeOffset? LastSync { get; set; }

    /// <summary>Gets or sets the last successful full discovery time.</summary>
    public DateTimeOffset? LastFullSync { get; set; }

    /// <summary>Gets the owned entries.</summary>
    public Dictionary<string, CatalogueEntry> Entries { get; init; } = new(StringComparer.Ordinal);
}
