using MediathekView.Api.Models;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>An identified logical film or episode and its current source variant.</summary>
/// <param name="Key">Stable logical identity.</param>
/// <param name="Title">Cleaned title.</param>
/// <param name="Topic">Actual programme name.</param>
/// <param name="Year">Known production year.</param>
/// <param name="Language">Audio language.</param>
/// <param name="Season">Season number, if reliably parsed.</param>
/// <param name="Episode">Episode number, if reliably parsed.</param>
/// <param name="Source">Current upstream record.</param>
public record CatalogueCandidate(string Key, string Title, string Topic, int? Year, string Language, int? Season, int? Episode, ResultItemDto Source);
