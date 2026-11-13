using System;
using System.Collections.Generic;
using System.Linq;
using MediathekView.Api.External;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Selects source variants without merging different years or known audio languages.</summary>
public static class CatalogueSelection
{
    /// <summary>Selects exact-link and confidently normalized duplicates.</summary>
    /// <param name="items">Prepared candidates.</param>
    /// <param name="existing">Existing owned identities, preferred to keep paths stable.</param>
    /// <param name="duplicates">Original keys that are duplicate sources, excluding retained keys.</param>
    /// <param name="previousUrls">Previously selected URLs, used to preserve ambiguous existing items.</param>
    /// <returns>Selected logical items.</returns>
    public static IReadOnlyList<CatalogueCandidate> Select(IEnumerable<CatalogueCandidate> items, ISet<string> existing, out HashSet<string> duplicates, IReadOnlyDictionary<string, string>? previousUrls = null)
    {
        var all = items.ToList();
        var exact = all.GroupBy(i => i.Season.HasValue ? "episode:" + i.Key : "url:" + (i.Source.GetVideoByQuality()?.Url ?? i.Key), StringComparer.Ordinal)
            .Select(g => Choose(g, existing)).ToList();
        var selected = exact.GroupBy(i => i.DuplicateKey ?? i.Key, StringComparer.Ordinal).Select(g => Choose(g, existing)).ToList();
        // Legacy title-based keys can collide for genuinely distinct productions.
        // Keep any existing files intact and leave such identities unresolved.
        var conflicts = selected.GroupBy(i => i.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var retained = selected.Where(i => conflicts.Contains(i.Key) && previousUrls is not null
            && previousUrls.TryGetValue(i.Key, out var url) && url == i.Source.GetVideoByQuality()?.Url).GroupBy(i => i.Key, StringComparer.Ordinal).Select(g => Choose(g, existing)).ToList();
        selected = selected.Where(i => !conflicts.Contains(i.Key)).ToList();
        selected.AddRange(retained);
        var kept = selected.Select(i => i.Key).Concat(conflicts).ToHashSet(StringComparer.Ordinal);
        duplicates = all.Select(i => i.Key).Where(k => !kept.Contains(k)).ToHashSet(StringComparer.Ordinal);
        return selected;
    }

    private static CatalogueCandidate Choose(IEnumerable<CatalogueCandidate> items, ISet<string> existing)
        => items.OrderByDescending(i => i.Source.GetVideoByQuality()?.Quality ?? 0)
            .ThenByDescending(i => existing.Contains(i.Key))
            .ThenByDescending(i => i.Language is not ("und" or ""))
            .ThenBy(i => i.Key, StringComparer.Ordinal).First();
}
