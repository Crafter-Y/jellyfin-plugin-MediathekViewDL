using System;
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
            var excluded = Regex.IsMatch(item.Topic + " " + title, @"\b(?:Trailer|Interview|Making.of|Nachrichten|Tagesschau|Magazin|Reportage|Dokumentation|Doku|Sportschau)\b", RegexOptions.IgnoreCase);
            var fiction = Regex.IsMatch(description, @"\b(?:Spielfilm|Fernsehfilm|Filmkom[oö]die|Liebesfilm|Kriminalfilm|Science.Fiction.Film|Kinofilm)\b", RegexOptions.IgnoreCase);
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
        return new CatalogueCandidate(key, title, item.Topic, year, language, series ? video.SeasonNumber : null, series ? video.EpisodeNumber : null, item);
    }

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
}
