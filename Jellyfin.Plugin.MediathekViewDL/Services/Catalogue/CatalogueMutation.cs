using System;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>A durable, small catalogue change between full manifest checkpoints.</summary>
/// <param name="Generation">The manifest generation this change belongs to.</param>
/// <param name="Key">Stable catalogue identity.</param>
/// <param name="Entry">Updated entry, or null for retirement.</param>
internal sealed record CatalogueMutation(Guid Generation, string Key, CatalogueEntry? Entry);
