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
    private readonly ILogger<CatalogueSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="CatalogueSyncService"/> class.</summary>
    /// <param name="api">Broadcaster catalogue client.</param>
    /// <param name="configuration">Live plugin configuration.</param>
    /// <param name="parser">Programme numbering parser.</param>
    /// <param name="clients">HTTP client factory.</param>
    /// <param name="library">Jellyfin library manager.</param>
    /// <param name="logger">Structured logger.</param>
    public CatalogueSyncService(IMediathekViewApiClient api, IConfigurationProvider configuration, IVideoParser parser, IHttpClientFactory clients, ILibraryManager library, ILogger<CatalogueSyncService> logger)
    {
        _api = api;
        _configuration = configuration;
        _parser = parser;
        _clients = clients;
        _library = library;
        _logger = logger;
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
            var full = !store.State.LastFullSync.HasValue || now - store.State.LastFullSync.Value >= TimeSpan.FromHours(24);
            var since = full ? (DateTimeOffset?)null : store.State.LastSync?.AddDays(-7);
            var candidates = await DiscoverAsync(options, since, cancellationToken, progress).ConfigureAwait(false);
            var selected = candidates.GroupBy(c => c.Key, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(c => c.Source.GetVideoByQuality()?.Quality ?? 0).ThenByDescending(c => c.Source.Timestamp).First()).ToList();
            var seen = selected.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var written = 0;
            var processed = 0;
            foreach (var candidate in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = candidate.Source.GetVideoByQuality()?.Url;
                if (url is not null && IsAllowedStream(url) && store.Upsert(candidate, url, now))
                {
                    written++;
                }

                if (++processed % 50 == 0)
                {
                    progress.Report(40 + (50.0 * processed / Math.Max(1, selected.Count)));
                }
            }

            var retired = 0;
            // Only a successfully completed FULL discovery is allowed to probe absent items.
            // Absence alone, HTTP 403/429/5xx and network failures never retire anything.
            if (full)
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

            store.State.LastSync = now;
            store.Save();
            if (written > 0 || retired > 0)
            {
                _library.QueueLibraryScan();
            }

            _logger.LogInformation("Catalogue sync completed: full={Full}, candidates={Candidates}, managed={Managed}, retired={Retired}", full, candidates.Count, written, retired);
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
