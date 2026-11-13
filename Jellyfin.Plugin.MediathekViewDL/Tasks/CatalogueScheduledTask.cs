using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediathekViewDL.Tasks;

/// <summary>Automatically refreshes the managed streaming libraries.</summary>
public sealed class CatalogueScheduledTask : IScheduledTask
{
    private readonly CatalogueSyncService _sync;

    /// <summary>Initializes a new instance of the <see cref="CatalogueScheduledTask"/> class.</summary>
    /// <param name="sync">Catalogue synchronizer.</param>
    public CatalogueScheduledTask(CatalogueSyncService sync) => _sync = sync;

    /// <inheritdoc />
    public string Name => "Mediathek Streaming-Katalog synchronisieren";

    /// <inheritdoc />
    public string Key => "MediathekAutomaticCatalogue";

    /// <inheritdoc />
    public string Description => "Aktualisiert Streaming-Links und Metadaten; keine Video-Downloads. Abgelaufene Links werden nach wiederholter Prüfung archiviert.";

    /// <inheritdoc />
    public string Category => "Mediathek Downloader";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _sync.SyncAsync(progress, cancellationToken);
}
