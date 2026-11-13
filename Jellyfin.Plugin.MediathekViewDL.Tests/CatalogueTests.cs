using Jellyfin.Plugin.MediathekViewDL.Configuration;
using Jellyfin.Plugin.MediathekViewDL.Configuration.Groups;
using Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;
using Jellyfin.Plugin.MediathekViewDL.Services.Media;
using MediaBrowser.Controller.Library;
using MediathekView.Api.External;
using MediathekView.Api.Models;
using MediathekView.Api.Models.ResourceItem;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MediathekViewDL.Tests;

public class CatalogueTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mediathek-test-" + Guid.NewGuid().ToString("N"));
    private readonly VideoParser _parser = new(NullLogger<VideoParser>.Instance, new LanguageDetectionService());

    public CatalogueTests() => Directory.CreateDirectory(_temp);

    [Theory]
    [InlineData("A film", "Spielfilm Deutschland 1998", true, 1998)]
    [InlineData("A film", "Fernsehfilm Frankreich 2020", true, 2020)]
    [InlineData("A film", "Spielfilm. Eine Geschichte ohne bekanntes Produktionsjahr.", true, null)]
    [InlineData("A film", "Ein interessanter Beitrag", false, null)]
    [InlineData("Trailer zum Film", "Spielfilm Deutschland 2020", false, null)]
    [InlineData("Interview", "Spielfilm Deutschland 2020", false, null)]
    [InlineData("Doku", "Spielfilm Deutschland 2020", false, null)]
    [InlineData("Episode (S01/E02)", "Fernsehserie Deutschland 2020", false, null)]
    public void Classify_ShouldRequirePositiveFilmEvidence(string title, string description, bool expected, int? year)
    {
        // Arrange
        var item = Item(title, description);
        // Act
        var result = CataloguePolicy.Classify(item, _parser, false);
        // Assert
        Assert.Equal(expected, result is not null);
        if (expected)
        {
            Assert.Equal(year, result!.Year);
        }
    }

    [Fact]
    public void Classify_ShouldDeduplicateBroadcasterVariants()
    {
        // Arrange
        var first = Item("A film", "Spielfilm Deutschland 1998");
        var second = first with { Id = "other-broadcast", Channel = "ZDF" };
        // Act
        var left = CataloguePolicy.Classify(first, _parser, false);
        var right = CataloguePolicy.Classify(second, _parser, false);
        // Assert
        Assert.Equal(left!.Key, right!.Key);
    }

    [Fact]
    public void Store_ShouldGroupSeriesByActualProgramme_AndPreserveUserEdits()
    {
        // Arrange
        var item = CataloguePolicy.Classify(Item("Episode (S01/E02)", "Fernsehserie Deutschland 2020") with { Topic = "Actual show" }, _parser, true)!;
        var store = new CatalogueStore(Path.Combine(_temp, "library"), Path.Combine(_temp, "state"));
        // Act
        Assert.True(store.Upsert(item, "https://example.ard.de/one.mp4", DateTimeOffset.UtcNow));
        var link = Path.Combine(store.State.Root, store.State.Entries[item.Key].RelativePath);
        File.WriteAllText(link, "https://user-edited.invalid/video");
        var updated = store.Upsert(item, "https://example.ard.de/two.mp4", DateTimeOffset.UtcNow);
        // Assert
        Assert.Contains("Actual show", link);
        Assert.Contains("Season 01", link);
        Assert.Contains("S01E02", link);
        Assert.False(updated);
        Assert.Equal("https://user-edited.invalid/video", File.ReadAllText(link));
    }

    [Fact]
    public void Store_ShouldRetireOnlyAfterIndependentFailuresAndGrace_WithoutTouchingVideo()
    {
        // Arrange
        var item = CataloguePolicy.Classify(Item("A film", "Spielfilm Deutschland 1998"), _parser, false)!;
        var root = Path.Combine(_temp, "library");
        var data = Path.Combine(_temp, "state");
        var store = new CatalogueStore(root, data);
        var now = DateTimeOffset.UtcNow;
        store.Upsert(item, "https://example.ard.de/film.mp4", now);
        var link = Path.Combine(root, store.State.Entries[item.Key].RelativePath);
        var video = Path.Combine(Path.GetDirectoryName(link)!, "personal.mkv");
        File.WriteAllText(video, "user video");
        // Act
        Assert.False(store.MarkUnavailable(item.Key, now, 72));
        Assert.False(store.MarkUnavailable(item.Key, now, 72));
        Assert.False(store.MarkUnavailable(item.Key, now.AddDays(1), 72));
        Assert.False(store.MarkUnavailable(item.Key, now.AddDays(2), 72));
        var retired = store.MarkUnavailable(item.Key, now.AddDays(3), 72);
        // Assert
        Assert.True(retired);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(video));
        Assert.Single(Directory.GetFiles(Path.Combine(data, "retired"), "*.strm", SearchOption.AllDirectories));
        Assert.Empty(new CatalogueStore(root, data).State.Entries);
    }

    [Fact]
    public void Store_ShouldResetFailuresOnRecovery_AndRejectUnclaimedOrEscapingPaths()
    {
        // Arrange
        var item = CataloguePolicy.Classify(Item("A film", "Spielfilm Deutschland 1998"), _parser, false)!;
        var root = Path.Combine(_temp, "library");
        var store = new CatalogueStore(root, Path.Combine(_temp, "state"));
        var now = DateTimeOffset.UtcNow;
        store.Upsert(item, "https://example.ard.de/film.mp4", now);
        // Act
        store.MarkUnavailable(item.Key, now, 72);
        store.MarkAvailable(item.Key);
        // Assert
        Assert.Equal(0, store.State.Entries[item.Key].MissingChecks);
        Assert.Null(store.State.Entries[item.Key].MissingSince);
        store.State.Entries[item.Key].RelativePath = "../outside.strm";
        Assert.Throws<IOException>(() => store.Upsert(item, "https://example.ard.de/film.mp4", now));
        Assert.Throws<ArgumentException>(() => new CatalogueStore(root, Path.Combine(root, "private-state")));
        var unclaimed = Path.Combine(_temp, "unclaimed");
        Directory.CreateDirectory(unclaimed);
        File.WriteAllText(Path.Combine(unclaimed, "personal.mkv"), "untouched");
        Assert.Throws<IOException>(() => new CatalogueStore(unclaimed, Path.Combine(_temp, "other-state")));
    }

    [Fact]
    public void Store_ShouldNotRewriteUnchangedLinks_OrOverwriteEditedMetadata()
    {
        // Arrange
        var item = CataloguePolicy.Classify(Item("A film", "Spielfilm Deutschland 1998"), _parser, false)!;
        var store = new CatalogueStore(Path.Combine(_temp, "library"), Path.Combine(_temp, "state"));
        var now = DateTimeOffset.UtcNow;
        store.Upsert(item, "https://example.ard.de/film.mp4", now);
        var path = Path.Combine(store.State.Root, store.State.Entries[item.Key].RelativePath);
        var modified = File.GetLastWriteTimeUtc(path);
        // Act
        var rewritten = store.Upsert(item, "https://example.ard.de/film.mp4", now.AddHours(6));
        File.WriteAllText(Path.ChangeExtension(path, ".nfo"), "user metadata");
        var replaced = store.Upsert(item, "https://example.ard.de/new.mp4", now.AddDays(1));
        // Assert
        Assert.False(rewritten);
        Assert.False(replaced);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.StartsWith("https://example.ard.de/film.mp4", File.ReadAllText(path));
        Assert.Equal("user metadata", File.ReadAllText(Path.ChangeExtension(path, ".nfo")));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Discover_ShouldHandleLowerBounds_Checkpoints_AndChangedSnapshots(bool lowerBound, bool overlap, bool changedSnapshot)
    {
        // Arrange
        var api = new Mock<IMediathekViewApiClient>();
        var configuration = new Mock<IConfigurationProvider>();
        configuration.SetupGet(c => c.Configuration).Returns(new PluginConfiguration());
        var now = DateTimeOffset.UtcNow;
        var offsets = new List<int>();
        api.Setup(a => a.SearchPageAsync(It.IsAny<ApiQueryDto>(), It.IsAny<CancellationToken>()))
            .Returns((ApiQueryDto query, CancellationToken _) =>
            {
                offsets.Add(query.Offset);
                return Task.FromResult(new QueryResultDto
                {
                    QueryInfo = new QueryInfoDto
                    {
                        MovieListTimestamp = changedSnapshot ? query.Offset : 1,
                        ResultCount = query.Offset < 2 ? 1 : 0,
                        TotalResults = 10000,
                        TotalRelation = lowerBound ? TotalRelation.GreaterThan : TotalRelation.Equal,
                    },
                    Results = query.Offset < 2 ? [Item("Film " + query.Offset, "Spielfilm Deutschland 1998") with { Timestamp = now.AddDays(-query.Offset * 20) }] : [],
                });
            });
        using var service = new CatalogueSyncService(api.Object, configuration.Object, _parser,
            Mock.Of<IHttpClientFactory>(), Mock.Of<ILibraryManager>(), NullLogger<CatalogueSyncService>.Instance);
        // Act
        if (changedSnapshot)
        {
            await Assert.ThrowsAsync<IOException>(() => service.DiscoverAsync(new CatalogueOptions { Channels = "ARD", PageSize = 1 }, null, CancellationToken.None));
        }
        else
        {
            var result = await service.DiscoverAsync(new CatalogueOptions { Channels = "ARD", PageSize = 1 }, overlap ? now.AddDays(-7) : null, CancellationToken.None);
            // Assert
            Assert.Equal(2, result.Count);
        }

        Assert.Equal(overlap || changedSnapshot ? new[] { 0, 1 } : new[] { 0, 1, 2 }, offsets);
    }

    [Fact]
    public async Task Pager_ShouldPartitionOversizedQueries_WithoutLosingBoundarySeconds()
    {
        // Arrange
        var api = new Mock<IMediathekViewApiClient>();
        var durations = new[] { 600, 2398, 2399, 2400, 4198, 4199, 4200, 6000 };
        var calls = new List<ApiQueryDto>();
        api.Setup(a => a.SearchPageAsync(It.IsAny<ApiQueryDto>(), It.IsAny<CancellationToken>()))
            .Returns((ApiQueryDto query, CancellationToken _) =>
            {
                calls.Add(query);
                var oversized = !query.MaxDuration.HasValue && query.MinDuration == 599;
                var selected = durations.Where(d => d > query.MinDuration && (!query.MaxDuration.HasValue || d < query.MaxDuration.Value)).ToList();
                var page = selected.Skip(query.Offset).Take(query.Size).ToList();
                return Task.FromResult(new QueryResultDto
                {
                    QueryInfo = new QueryInfoDto { MovieListTimestamp = 1, ResultCount = page.Count, TotalResults = oversized ? 10000 : selected.Count, TotalRelation = oversized ? TotalRelation.GreaterThan : TotalRelation.Equal },
                    Results = page.Select(d => Item("Film " + d, "Spielfilm Deutschland 1998") with { Duration = TimeSpan.FromSeconds(d) }).ToList(),
                });
            });
        var found = new List<int>();
        // Act
        await foreach (var page in new CataloguePager(api.Object, TimeSpan.Zero).GetPagesAsync(new ApiQueryDto { MinDuration = 599, Size = 2 }, 20, null, CancellationToken.None))
        {
            found.AddRange(page.Results.Select(i => (int)i.Duration.TotalSeconds));
        }

        // Assert
        Assert.Equal(durations, found.Order());
        Assert.Contains(calls, q => q.MaxDuration == 4199);
        Assert.Contains(calls, q => q.MinDuration == 4198);
        Assert.All(calls, q => Assert.True(q.Offset < 10000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discover_ShouldTraversePages_AndFailOnPartialSnapshot(bool failSecondPage)
    {
        // Arrange
        var api = new Mock<IMediathekViewApiClient>();
        var configuration = new Mock<IConfigurationProvider>();
        configuration.SetupGet(c => c.Configuration).Returns(new PluginConfiguration());
        var offsets = new List<int>();
        api.Setup(a => a.SearchPageAsync(It.IsAny<ApiQueryDto>(), It.IsAny<CancellationToken>()))
            .Returns((ApiQueryDto query, CancellationToken _) =>
            {
                offsets.Add(query.Offset);
                if (query.Offset == 1 && failSecondPage)
                {
                    throw new IOException("upstream failed");
                }

                return Task.FromResult(new QueryResultDto
                {
                    QueryInfo = new QueryInfoDto { MovieListTimestamp = 1, TotalResults = 2, ResultCount = 1 },
                    Results = [Item("Film " + query.Offset, "Spielfilm Deutschland 1998")],
                });
            });
        using var service = new CatalogueSyncService(api.Object, configuration.Object, _parser,
            Mock.Of<IHttpClientFactory>(), Mock.Of<ILibraryManager>(), NullLogger<CatalogueSyncService>.Instance);
        var options = new CatalogueOptions { Channels = "ARD", PageSize = 1, MaxPages = 5 };
        // Act
        if (failSecondPage)
        {
            await Assert.ThrowsAsync<IOException>(() => service.DiscoverAsync(options, null, CancellationToken.None));
        }
        else
        {
            var result = await service.DiscoverAsync(options, null, CancellationToken.None);
            // Assert
            Assert.Equal(2, result.Count);
        }

        Assert.Equal(new[] { 0, 1 }, offsets);
    }

    private static ResultItemDto Item(string title, string description) => new()
    {
        Id = "id-" + title, Title = title, Topic = "Filme im Ersten", Channel = "ARD", Description = description,
        Duration = TimeSpan.FromMinutes(90), Timestamp = DateTimeOffset.UtcNow,
        VideoUrls = [new VideoUrlDto { Url = "https://example.ard.de/film.mp4", Quality = 3 }], SubtitleUrls = [], ExternalIds = [],
    };

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, true);
        }
    }
}
