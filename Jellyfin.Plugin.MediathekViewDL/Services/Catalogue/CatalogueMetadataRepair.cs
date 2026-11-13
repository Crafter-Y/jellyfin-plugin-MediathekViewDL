using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Removes misleading movie matches from catalogue-owned contextual TV-film entries.</summary>
public sealed class CatalogueMetadataRepair
{
    private readonly ILibraryManager _library;
    private Dictionary<string, Movie>? _movies;

    /// <summary>Initializes a new instance of the <see cref="CatalogueMetadataRepair"/> class.</summary>
    /// <param name="library">Jellyfin library API.</param>
    public CatalogueMetadataRepair(ILibraryManager library) => _library = library;

    /// <summary>Repairs only the specified owned item, preserving its identity and playback history.</summary>
    /// <param name="candidate">Authoritative source description.</param>
    /// <param name="ownedPath">Already verified, catalogue-owned file path.</param>
    /// <param name="backupDirectory">Private backup directory.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether an existing Jellyfin item was repaired.</returns>
    public async Task<bool> RepairAsync(CatalogueCandidate candidate, string ownedPath, string backupDirectory, CancellationToken cancellationToken)
    {
        if (!candidate.UseSourceMetadata)
        {
            return false;
        }

        _movies ??= _library.GetItemList(new InternalItemsQuery { Recursive = true, IncludeItemTypes = [BaseItemKind.Movie] })
            .OfType<Movie>().Where(m => !string.IsNullOrEmpty(m.Path)).GroupBy(m => m.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (!_movies.TryGetValue(ownedPath, out var movie))
        {
            return false;
        }

        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, candidate.Key + ".json");
        if (!File.Exists(backup))
        {
            await File.WriteAllTextAsync(
                backup,
                JsonSerializer.Serialize(new
                {
                    movie.Id, movie.Name, movie.OriginalTitle, movie.ProviderIds, movie.ImageInfos,
                    movie.Overview, movie.ProductionYear, movie.CommunityRating, movie.CriticRating,
                    movie.OfficialRating, movie.Studios, movie.Genres, movie.Tags, movie.RemoteTrailers,
                    movie.Tagline, movie.PresentationUniqueKey, movie.TmdbCollectionName,
                    People = _library.GetPeople(movie),
                }),
                cancellationToken).ConfigureAwait(false);
        }

        movie.Name = candidate.Title;
        movie.OriginalTitle = candidate.Title;
        movie.Overview = candidate.Source.Description;
        movie.ProductionYear = candidate.Year;
        movie.PremiereDate = null;
        movie.RunTimeTicks = candidate.Source.Duration.Ticks;
        movie.IsLocked = true;
        movie.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mediathek-catalogue"] = candidate.Key };
        movie.ImageInfos = [];
        movie.CommunityRating = null;
        movie.CriticRating = null;
        movie.OfficialRating = null;
        movie.Studios = [candidate.Source.Channel];
        movie.Genres = [];
        movie.Tags = [];
        movie.RemoteTrailers = [];
        movie.Tagline = null;
        movie.TmdbCollectionName = null;
        movie.PresentationUniqueKey = string.Empty;
        await _library.UpdatePeopleAsync(movie, [], cancellationToken).ConfigureAwait(false);
        await _library.UpdateItemAsync(movie, movie.GetParent() ?? _library.RootFolder, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
