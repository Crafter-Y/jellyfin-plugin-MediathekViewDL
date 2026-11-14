using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediathekViewDL.Services.Media;
using MediathekView.Api.Models;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Classifies confidently identified films and numbered episodes without guessing unknown content.</summary>
public static class CataloguePolicy
{
    /// <summary>Classifies one API record, or returns null for unsupported or ambiguous content.</summary>
    /// <param name="item">The broadcaster record.</param>
    /// <param name="parser">The episode parser.</param>
    /// <param name="includeSeries">Whether numbered episodes are wanted.</param>
    /// <returns>A stable logical catalogue item, independent of broadcaster/quality variants.</returns>
    public static CatalogueCandidate? Classify(ResultItemDto item, IVideoParser parser, bool includeSeries)
    {
        var video = parser.ParseVideoInfo(item.Topic, item.Title);
        if (video is null || video.HasAudiodescription || video.HasSignLanguage || video.HasClearLanguage || video.IsTrailer || video.IsInterview)
        {
            return null;
        }

        var title = video.Title.Trim();
        if (title.Length == 0 || item.Duration < TimeSpan.FromMinutes(10))
        {
            return null;
        }

        var description = item.Description;
        var explicitSeries = Regex.IsMatch(description, @"\b(?:Fernsehserie|Krimiserie|Dramaserie|Comedyserie|Staffel|Episode)\b", RegexOptions.IgnoreCase);
        var series = video.HasSeasonEpisodeNumbering;
        if (series || video.IsShow || explicitSeries)
        {
            if (!includeSeries || !series || string.IsNullOrWhiteSpace(item.Topic))
            {
                return null;
            }
        }
        else
        {
            // No numbering is not proof of a movie. Require positive fiction evidence,
            // and reject clips, factual programmes and documentaries conservatively.
            var excluded = Regex.IsMatch(item.Topic + " " + title, @"\b(?:Trailer|Interview|Making.of|Nachrichten|Tagesschau|Magazin|Reportage|Dokumentation|Dokumentarfilm|Doku|Sportschau)\b", RegexOptions.IgnoreCase);
            var fiction = HasFilmEvidence(description);
            if (excluded || !fiction || item.Duration < TimeSpan.FromMinutes(50))
            {
                return null;
            }
        }

        var yearMatch = Regex.Match(description, @"\b(?:Deutschland|Frankreich|USA|Großbritannien|Österreich|Schweiz|Italien|Kanada|Australien|Schweden|Norwegen|Dänemark)(?:\s*[/,]\s*\p{L}+)*\s+((?:19|20)\d{2})\b", RegexOptions.IgnoreCase);
        int? year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        // Unknown production years remain unknown; broadcast dates are never production dates.
        var language = string.IsNullOrWhiteSpace(video.Language) ? "deu" : video.Language;
        var identity = series
            ? FormattableString.Invariant($"series|{item.Topic}|{video.SeasonNumber}|{video.EpisodeNumber}|{language}")
            : FormattableString.Invariant($"movie|{title}|{year}|{language}");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(identity)))).ToLowerInvariant();
        // Topic removal is appropriate for numbered episode names, but changes a
        // TV film such as "Tatort: Avatar" into the unrelated movie query "Avatar".
        // Preserve existing identities/paths while repairing the displayed metadata.
        var completeTitle = !series ? parser.ParseVideoInfo(null, item.Title)?.Title.Trim() ?? title : title;
        var contextualFilm = !series && !string.Equals(StripContext(completeTitle, item.Topic), completeTitle, StringComparison.Ordinal);
        return new CatalogueCandidate(key, series ? title : completeTitle, item.Topic, year, language, series ? video.SeasonNumber : null, series ? video.EpisodeNumber : null, item, contextualFilm);
    }

    /// <summary>Normalizes broadcaster punctuation and equivalent prefix/suffix programme labels.</summary>
    /// <param name="items">Candidates from completed discovery.</param>
    /// <param name="knownTopics">Previously identified contextual TV-film programmes.</param>
    /// <returns>Prepared candidates with conservative duplicate identities.</returns>
    public static IReadOnlyList<CatalogueCandidate> PrepareMovies(IEnumerable<CatalogueCandidate> items, ISet<string> knownTopics)
    {
        var list = items.ToList();
        var contextualTopics = new HashSet<string>(knownTopics, StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.Where(i => i.UseSourceMetadata))
        {
            knownTopics.Add(item.Topic);
            contextualTopics.Add(item.Topic);
        }

        return list.Select(item =>
        {
            if (item.Season.HasValue)
            {
                return item;
            }

            var topic = contextualTopics.Contains(item.Topic) ? item.Topic
                : contextualTopics.FirstOrDefault(t => StripContext(item.Title, t) != item.Title) ?? item.Topic;
            var contextual = item.UseSourceMetadata || contextualTopics.Contains(topic);
            var core = StripContext(item.Title, topic);
            var title = contextual ? topic + ": " + core : item.Title;
            var description = NormalizeMovie(item.Source.Description);
            // Matching names alone are insufficient: require matching plot evidence
            // and a comparable cut length, or the exact same video URL below.
            var evidence = description.Length >= 80 ? description[..80] : item.Source.Id;
            var cut = Math.Round(item.Source.Duration.TotalMinutes / 5);
            var duplicate = NormalizeMovie(title) + FormattableString.Invariant($"|{item.Year}|{item.Language}|{cut}|{evidence}");
            return item with { Title = title, UseSourceMetadata = contextual, DuplicateKey = duplicate };
        }).ToList();
    }

    /// <summary>Requires a production/type label, rather than a passing reference to a film in prose.</summary>
    /// <param name="description">Broadcaster description.</param>
    /// <returns>Whether a film type is explicitly labelled.</returns>
    public static bool HasFilmEvidence(string description)
        => Regex.IsMatch(description, @"(?:^|[\r\n|·])\s*(?:Spielfilm|Fernsehfilm|Filmkom[oö]die|Liebesfilm|Kriminalfilm|Science.Fiction.Film|Kinofilm)\b", RegexOptions.IgnoreCase);

    /// <summary>Creates a filesystem-safe, bounded component.</summary>
    /// <param name="name">The human readable name.</param>
    /// <returns>A portable directory or file name.</returns>
    public static string SafeName(string name)
    {
        var safe = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
        if (safe is "" or "." or "..")
        {
            safe = "Untitled";
        }

        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToUpperInvariant(), @"\s+", " ").Trim();

    private static string NormalizeMovie(string text) => Regex.Replace(Normalize(text), @"[^\p{L}\p{Nd}]+", " ").Trim();

    private static string StripContext(string title, string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return title;
        }

        var prefix = Regex.Match(title, @"^" + Regex.Escape(topic) + @"\s*[:–—|\-]\s*(.+)$", RegexOptions.IgnoreCase);
        var suffix = Regex.Match(title, @"^(.+?)\s*[:–—|\-]\s*" + Regex.Escape(topic) + @"$", RegexOptions.IgnoreCase);
        return prefix.Success ? prefix.Groups[1].Value.Trim() : suffix.Success ? suffix.Groups[1].Value.Trim() : title;
    }
}
