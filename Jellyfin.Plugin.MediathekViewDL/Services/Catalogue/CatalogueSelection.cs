using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FuzzySharp;
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
        var selected = new List<CatalogueCandidate>();
        foreach (var group in exact.GroupBy(i => BaseIdentity(i.DuplicateKey) ?? i.Key, StringComparer.Ordinal))
        {
            var remaining = group.ToList();
            while (remaining.Count > 0)
            {
                var anchor = Choose(remaining, existing);
                selected.Add(anchor);
                remaining.RemoveAll(i => i == anchor || AreEquivalent(anchor.DuplicateKey, anchor.Source.Description, i.DuplicateKey, i.Source.Description));
            }
        }

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

    /// <summary>Compares plot evidence only for identical normalized title, year, language and cut buckets.</summary>
    /// <param name="firstKey">First normalized duplicate identity.</param>
    /// <param name="firstPlot">First full broadcaster description.</param>
    /// <param name="secondKey">Second normalized duplicate identity.</param>
    /// <param name="secondPlot">Second full broadcaster description.</param>
    /// <returns>Whether the two sources confidently describe the same film.</returns>
    public static bool AreEquivalent(string? firstKey, string firstPlot, string? secondKey, string secondPlot)
    {
        if (firstKey is null || secondKey is null || BaseIdentity(firstKey) != BaseIdentity(secondKey))
        {
            return false;
        }

        if (firstKey == secondKey || (firstPlot.Length >= 100 && secondPlot.Length >= 100
            && Fuzz.TokenSetRatio(firstPlot, secondPlot) >= 95 && Fuzz.Ratio(firstPlot, secondPlot) >= 85))
        {
            return true;
        }

        // Broadcasters sometimes supply different plot summaries after an identical
        // opening sentence. Require substantial shared prose, beyond the production label.
        var firstLead = PlotLead(firstPlot);
        var secondLead = PlotLead(secondPlot);
        return firstLead.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 12
            && secondLead.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 12
            && Fuzz.TokenSetRatio(firstLead, secondLead) == 100 && Fuzz.Ratio(firstLead, secondLead) >= 65;
    }

    private static string? BaseIdentity(string? key) => key is null ? null : string.Join('|', key.Split('|').Take(4));

    private static string PlotLead(string description)
    {
        var prose = Regex.Replace(description, @"^(?:Spielfilm|Fernsehfilm)\s+[^\r\n]*?\b(?:19|20)\d{2}\s*", string.Empty, RegexOptions.IgnoreCase);
        var lead = Regex.Split(prose, @"[.!?:\r\n]", RegexOptions.None).FirstOrDefault() ?? string.Empty;
        return Regex.Replace(lead.ToUpperInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
    }

    private static CatalogueCandidate Choose(IEnumerable<CatalogueCandidate> items, ISet<string> existing)
        => items.OrderByDescending(i => i.Source.GetVideoByQuality()?.Quality ?? 0)
            .ThenByDescending(i => existing.Contains(i.Key))
            .ThenByDescending(i => i.Language is not ("und" or ""))
            .ThenBy(i => i.Key, StringComparer.Ordinal).First();
}
