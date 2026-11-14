using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediathekViewDL.Configuration;
using Jellyfin.Plugin.MediathekViewDL.Configuration.Groups;
using Jellyfin.Plugin.MediathekViewDL.Services.Media;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediathekView.Api.External;
using MediathekView.Api.Models;
using MediathekView.Api.Models.Enums;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Discovers and synchronizes streaming links without downloading programme videos.</summary>
public sealed class CatalogueSyncService : IDisposable
{
    private readonly IMediathekViewApiClient _api;
    private readonly IConfigurationProvider _configuration;
    private readonly IVideoParser _parser;
    private readonly IHttpClientFactory _clients;
    private readonly ILibraryManager _library;
    private readonly IItemPersistenceService _persistence;
    private readonly ILogger<CatalogueSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="CatalogueSyncService"/> class.</summary>
    /// <param name="api">Broadcaster catalogue client.</param>
    /// <param name="configuration">Live plugin configuration.</param>
    /// <param name="parser">Programme numbering parser.</param>
    /// <param name="clients">HTTP client factory.</param>
    /// <param name="library">Jellyfin library manager.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="persistence">Image database persistence.</param>
    public CatalogueSyncService(IMediathekViewApiClient api, IConfigurationProvider configuration, IVideoParser parser, IHttpClientFactory clients, ILibraryManager library, ILogger<CatalogueSyncService> logger, IItemPersistenceService persistence)
    {
        _api = api;
        _configuration = configuration;
        _parser = parser;
        _clients = clients;
        _library = library;
        _logger = logger;
        _persistence = persistence;
    }

    /// <summary>Runs discovery and safe reconciliation, with persisted incremental checkpoints.</summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The asynchronous sync operation.</returns>
    public async Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = _configuration.Configuration.Catalogue;
        if (!options.Enabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = new CatalogueStore(options.RootPath, Path.Combine(Plugin.Instance!.DataFolderPath, "catalogue"));
            var now = DateTimeOffset.UtcNow;
            var repairing = store.State.MetadataPolicyVersion < 3 && store.State.Entries.Count > 0;
            var full = !store.State.LastFullSync.HasValue || now - store.State.LastFullSync.Value >= TimeSpan.FromHours(24);
            var since = full ? (DateTimeOffset?)null : store.State.LastSync?.AddDays(-7);
            var candidates = await DiscoverAsync(repairing ? options with { IncludeSeries = false } : options, repairing ? null : since, cancellationToken, progress).ConfigureAwait(false);
            var prepared = CataloguePolicy.PrepareMovies(candidates, store.State.ContextualTopics);
            var selected = CatalogueSelection.Select(prepared, store.State.Entries.Keys.ToHashSet(StringComparer.Ordinal), out var duplicateKeys, store.State.Entries.ToDictionary(p => p.Key, p => p.Value.Url, StringComparer.Ordinal));
            var seen = selected.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var written = 0;
            var processed = 0;
            var metadata = new CatalogueMetadataRepair(_library, _persistence);
            var repaired = 0;
            foreach (var source in selected)
            {
                var candidate = source;
                cancellationToken.ThrowIfCancellationRequested();
                var url = candidate.Source.GetVideoByQuality()?.Url;
                // A newly published alias can match an older owned movie outside
                // the incremental window. Reuse that identity instead of adding a row.
                if (candidate.DuplicateKey is not null && !store.State.Entries.ContainsKey(candidate.Key))
                {
                    var anchor = store.State.Entries.FirstOrDefault(p => CatalogueSelection.AreEquivalent(p.Value.DuplicateKey, p.Value.SourceDescription, candidate.DuplicateKey, candidate.Source.Description)
                        || (p.Value.Url == url && p.Value.RelativePath.StartsWith("Movies" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
                    if (anchor.Value is not null)
                    {
                        candidate = candidate with { Key = anchor.Key };
                    }
                }

                seen.Add(candidate.Key);
                if (url is not null && IsAllowedStream(url) && store.Upsert(candidate, url, now))
                {
                    written++;
                }

                if (candidate.UseSourceMetadata && store.State.Entries.TryGetValue(candidate.Key, out var entry) && entry.MetadataGuardVersion < 3)
                {
                    var owned = store.GetOwnedPath(candidate.Key);
                    if (owned is not null && await metadata.RepairAsync(candidate, owned, Path.Combine(Plugin.Instance!.DataFolderPath, "catalogue", "metadata-repairs"), cancellationToken).ConfigureAwait(false))
                    {
                        store.MarkMetadataRepaired(candidate.Key);
                        repaired++;
                    }
                }

                if (++processed % 50 == 0)
                {
                    progress.Report(40 + (50.0 * processed / Math.Max(1, selected.Count)));
                }
            }

            var retired = 0;
            var duplicateCount = 0;
            foreach (var key in duplicateKeys)
            {
                if (!seen.Contains(key) && store.ArchiveDuplicate(key, now))
                {
                    duplicateCount++;
                }
            }

            // Only a successfully completed FULL discovery is allowed to probe absent items.
            // Absence alone, HTTP 403/429/5xx and network failures never retire anything.
            if (full && !repairing)
            {
                foreach (var pair in store.State.Entries.ToList())
                {
                    if (seen.Contains(pair.Key) || !IsAllowedStream(pair.Value.Url))
                    {
                        continue;
                    }

                    using var client = _clients.CreateClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Head, pair.Value.Url);
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                        {
                            if (store.MarkUnavailable(pair.Key, now, options.RetirementGraceHours))
                            {
                                retired++;
                            }
                        }
                        else if (response.IsSuccessStatusCode)
                        {
                            store.MarkAvailable(pair.Key);
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // A network outage is not evidence that a programme expired.
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Probe timeout: retain the item and its current failure history.
                    }
                }

                store.State.LastFullSync = now;
            }

            store.State.MetadataPolicyVersion = 3;
            if (!repairing)
            {
                store.State.LastSync = now;
            }

            store.Save();
            if (written > 0 || retired > 0 || duplicateCount > 0)
            {
                _library.QueueLibraryScan();
            }

            _logger.LogInformation("Catalogue sync completed: full={Full}, candidates={Candidates}, managed={Managed}, retired={Retired}", full, candidates.Count, written, retired);
            _logger.LogInformation("Catalogue metadata repair completed: migration={Migration}, repaired={Repaired}", repairing, repaired);
            _logger.LogInformation("Catalogue duplicates archived: {Duplicates}", duplicateCount);
            progress.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retrieves all selected broadcaster pages or fails without advancing the checkpoint.</summary>
    /// <param name="options">Discovery bounds and selection.</param>
    /// <param name="since">Incremental overlap start, or null for a full snapshot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="progress">Optional scheduled task progress.</param>
    /// <returns>Confidently classified candidates.</returns>
    public async Task<List<CatalogueCandidate>> DiscoverAsync(CatalogueOptions options, DateTimeOffset? since, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var channels = options.Channels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (channels.Length == 0)
        {
            throw new InvalidOperationException("Choose at least one broadcaster.");
        }

        var size = Math.Clamp(options.PageSize, 1, 500);
        var limit = Math.Clamp(options.MaxPages, 1, 10000);
        var knownChannels = await _api.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        channels = channels.Select(name => knownChannels.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown broadcaster: " + name)).ToArray();
        var candidates = new List<CatalogueCandidate>();
        var pager = new CataloguePager(_api);
        var completed = 0;
        foreach (var channel in channels)
        {
            var channelStart = candidates.Count;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await foreach (var result in pager.GetPagesAsync(
                        new ApiQueryDto
                        {
                            Queries = [new QueryFieldsDto { Fields = [QueryFieldType.ChannelExact], Query = channel }],
                            Size = size, MinDuration = options.IncludeSeries ? 599 : 2999, Future = true,
                        },
                        limit,
                        since,
                        cancellationToken).ConfigureAwait(false))
                    {
                        foreach (var item in result.Results.Where(i => string.Equals(i.Channel, channel, StringComparison.OrdinalIgnoreCase)))
                        {
                            var classified = CataloguePolicy.Classify(item, _parser, options.IncludeSeries);
                            if (classified is not null)
                            {
                                candidates.Add(classified);
                            }
                        }
                    }

                    break;
                }
                catch (IOException ex) when (attempt < 2 && ex.Message == "Upstream catalogue changed during pagination; checkpoint not committed.")
                {
                    candidates.RemoveRange(channelStart, candidates.Count - channelStart);
                    _logger.LogWarning("Catalogue changed while reading {Channel}; retrying its complete traversal ({Attempt}/2)", channel, attempt + 1);
                }
            }

            _logger.LogInformation("Catalogue discovery completed for {Channel}; {Candidates} accepted records so far", channel, candidates.Count);
            progress?.Report(40.0 * ++completed / channels.Length);
        }

        return candidates;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private bool IsAllowedStream(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && _configuration.Configuration.AllowedDomains.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
}
