using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediathekView.Api.Models;

namespace MediathekView.Api.External;

/// <summary>Traverses large catalogues within the upstream 10,000-result search window.</summary>
public sealed class CataloguePager
{
    private readonly IMediathekViewApiClient _client;
    private readonly TimeSpan _delay;

    /// <summary>Initializes a new instance of the <see cref="CataloguePager"/> class.</summary>
    /// <param name="client">API client.</param>
    /// <param name="requestDelay">Delay before each page; defaults to one second.</param>
    public CataloguePager(IMediathekViewApiClient client, TimeSpan? requestDelay = null)
    {
        _client = client;
        _delay = requestDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Partitions oversized searches by integer duration, then traverses every leaf page.</summary>
    /// <param name="query">Initial query. Duration boundaries are exclusive, as in the upstream API.</param>
    /// <param name="maxPages">Maximum total requests, including partition discovery.</param>
    /// <param name="since">Optional newest-first overlap checkpoint, applied separately to every partition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Complete leaf pages, or an exception for incomplete/unstable snapshots.</returns>
    public async IAsyncEnumerable<QueryResultDto> GetPagesAsync(ApiQueryDto query, int maxPages, DateTimeOffset? since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ranges = new Stack<(int Min, int? Max)>();
        ranges.Push((query.MinDuration ?? -1, query.MaxDuration));
        var requests = 0;
        long? snapshot = null;
        var size = Math.Clamp(query.Size, 1, 500);
        while (ranges.TryPop(out var range))
        {
            for (var offset = 0; ; offset = checked(offset + size))
            {
                if (++requests > maxPages)
                {
                    throw new IOException("Catalogue request guard reached; checkpoint not committed.");
                }

                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                var page = await _client.SearchPageAsync(query with { Offset = offset, Size = size, MinDuration = range.Min, MaxDuration = range.Max }, cancellationToken).ConfigureAwait(false);
                snapshot ??= page.QueryInfo.MovieListTimestamp;
                if (snapshot != page.QueryInfo.MovieListTimestamp)
                {
                    throw new IOException("Upstream catalogue changed during pagination; checkpoint not committed.");
                }

                if (offset == 0 && (page.QueryInfo.TotalRelation != TotalRelation.Equal || page.QueryInfo.TotalResults > 10000))
                {
                    // Upstream uses strict gt/lt bounds and integer seconds. The right
                    // range begins at split-1 so that no boundary second is lost.
                    if (range.Max.HasValue && range.Max.Value - range.Min <= 2)
                    {
                        throw new IOException("More than 10,000 records share one duration; a narrower query is required.");
                    }

                    var split = range.Max.HasValue ? range.Min + ((range.Max.Value - range.Min) / 2) : checked(range.Min + 3600);
                    ranges.Push((split - 1, range.Max));
                    ranges.Push((range.Min, split));
                    break;
                }

                yield return page;
                if (page.QueryInfo.ResultCount < size
                    || (page.QueryInfo.TotalRelation == TotalRelation.Equal && offset + size >= page.QueryInfo.TotalResults)
                    || (since.HasValue && page.Results.Count > 0 && page.Results.All(i => i.Timestamp < since.Value)))
                {
                    break;
                }

                if (offset + size >= 10000)
                {
                    throw new IOException("Upstream search window changed while traversing a partition.");
                }
            }
        }
    }
}
