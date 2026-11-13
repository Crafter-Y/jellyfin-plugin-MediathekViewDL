using Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;
using Jellyfin.Plugin.MediathekViewDL.Services.Media;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediathekView.Api.Models;
using MediathekView.Api.Models.ResourceItem;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MediathekViewDL.Tests;

public class CatalogueMatchingTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mediathek-matching-" + Guid.NewGuid().ToString("N"));
    private readonly VideoParser _parser = new(NullLogger<VideoParser>.Instance, new LanguageDetectionService());

    public CatalogueMatchingTests() => Directory.CreateDirectory(_temp);

    [Theory]
    [InlineData("Tatort: Avatar", "Tatort", "Avatar")]
    [InlineData("Tiroler Blut: Am Abgrund", "Tiroler Blut", "Am Abgrund")]
    public void MovieContext_ShouldPreserveFullTitle_AndRepairExistingPath(string title, string topic, string oldTitle)
    {
        // Arrange
        var candidate = Candidate(title, topic);
        var store = new CatalogueStore(Path.Combine(_temp, "library"), Path.Combine(_temp, "state"));
        store.Upsert(candidate with { Title = oldTitle, UseSourceMetadata = false }, "https://example.ard.de/film.mp4", DateTimeOffset.UtcNow);
        var oldPath = store.State.Entries[candidate.Key].RelativePath;
        // Act
        store.Upsert(candidate, "https://example.ard.de/film.mp4", DateTimeOffset.UtcNow);
        var metadata = File.ReadAllText(Path.ChangeExtension(Path.Combine(store.State.Root, oldPath), ".nfo"));
        // Assert
        Assert.Equal(title, candidate.Title);
        Assert.True(candidate.UseSourceMetadata);
        Assert.Equal(oldPath, store.State.Entries[candidate.Key].RelativePath);
        Assert.Contains("<lockdata>true</lockdata>", metadata);
        Assert.Contains("<title>" + title + "</title>", metadata);
    }

    [Fact]
    public void MovieDuplicates_ShouldMergePunctuation_AndProgrammePrefixSuffixVariants()
    {
        // Arrange
        var variants = new[]
        {
            Candidate("Billy Kuckuck: Aber bitte mit Sahne!", "Billy Kuckuck"),
            Candidate("Aber bitte mit Sahne! – Billy Kuckuck", "Billy Kuckuck"),
            Candidate("Aber bitte mit Sahne!", "Billy Kuckuck"),
        };
        var topics = new HashSet<string>();
        // Act
        var prepared = CataloguePolicy.PrepareMovies(variants, topics);
        var selected = CatalogueSelection.Select(prepared, new HashSet<string>(), out var duplicates);
        // Assert
        Assert.Single(selected);
        Assert.NotEmpty(duplicates);
        Assert.All(prepared, c => Assert.True(c.UseSourceMetadata));
        Assert.All(prepared, c => Assert.Equal("Billy Kuckuck: Aber bitte mit Sahne!", c.Title));
        Assert.Contains("Billy Kuckuck", topics);
    }

    [Fact]
    public void MovieDuplicates_ShouldKeepDifferentYearsAndKnownAudioLanguages()
    {
        // Arrange
        var original = Candidate("A film", "Films");
        var variants = new[]
        {
            original with { Key = "first", Year = 2001, Language = "deu" },
            Candidate("A film", "Films") with { Key = "remake", Year = 2020, Language = "deu" },
            Candidate("A film", "Films") with { Key = "english", Year = 2001, Language = "eng" },
        };
        // Act
        var prepared = CataloguePolicy.PrepareMovies(variants, new HashSet<string>());
        var selected = CatalogueSelection.Select(prepared, new HashSet<string>(), out var duplicates);
        // Assert
        Assert.Equal(3, selected.Count);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void MovieDuplicates_ShouldMergeExactVideoUrls_AndPreferExistingIdentity()
    {
        // Arrange
        var original = Candidate("A film", "Films");
        var alias = original with { Key = "alias", Title = "A differently labelled copy", Language = "und" };
        var prepared = CataloguePolicy.PrepareMovies(new[] { original, alias }, new HashSet<string>());
        // Act
        var selected = CatalogueSelection.Select(prepared, new HashSet<string> { original.Key }, out var duplicates);
        // Assert
        Assert.Equal(original.Key, Assert.Single(selected).Key);
        Assert.Contains(alias.Key, duplicates);
    }

    [Fact]
    public void MovieDuplicates_ShouldLeaveConflictingSameNameProductionsUnresolved()
    {
        // Arrange
        var first = Candidate("A film", "Films");
        var second = Candidate("A film", "Films");
        second = second with { Source = second.Source with { Description = "Fernsehfilm Deutschland 2024 Eine völlig andere Geschichte über Menschen in einer fremden Stadt und ihre besonderen Abenteuer." } };
        // Act
        var prepared = CataloguePolicy.PrepareMovies(new[] { first, second }, new HashSet<string>());
        var selected = CatalogueSelection.Select(prepared, new HashSet<string>(), out var duplicates);
        // Assert
        Assert.Empty(selected);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void ArchiveDuplicate_ShouldPreserveVideosAndUserEditedMetadata()
    {
        // Arrange
        var root = Path.Combine(_temp, "library");
        var data = Path.Combine(_temp, "state");
        var store = new CatalogueStore(root, data);
        var duplicate = Candidate("A duplicate", "Films");
        var edited = Candidate("An edited item", "Films");
        store.Upsert(duplicate, "https://example.ard.de/one.mp4", DateTimeOffset.UtcNow);
        store.Upsert(edited, "https://example.ard.de/two.mp4", DateTimeOffset.UtcNow);
        var link = Path.Combine(root, store.State.Entries[duplicate.Key].RelativePath);
        var video = Path.Combine(Path.GetDirectoryName(link)!, "personal.mkv");
        File.WriteAllText(video, "untouched");
        var editedLink = Path.Combine(root, store.State.Entries[edited.Key].RelativePath);
        File.WriteAllText(Path.ChangeExtension(editedLink, ".nfo"), "user edit");
        // Act
        var archived = store.ArchiveDuplicate(duplicate.Key, DateTimeOffset.UtcNow);
        var protectedItem = store.ArchiveDuplicate(edited.Key, DateTimeOffset.UtcNow);
        // Assert
        Assert.True(archived);
        Assert.False(protectedItem);
        Assert.True(File.Exists(video));
        Assert.True(File.Exists(editedLink));
        Assert.Single(Directory.GetFiles(Path.Combine(data, "retired", "duplicates"), "*.strm", SearchOption.AllDirectories));
        Assert.Single(new CatalogueStore(root, data).State.Entries);
    }

    [Fact]
    public async Task MetadataRepair_ShouldRemoveUnrelatedMovieData_OnlyFromTheOwnedItem()
    {
        // Arrange
        var candidate = Candidate("Tatort: Avatar", "Tatort");
        var path = Path.Combine(_temp, "Avatar.strm");
        var id = Guid.NewGuid();
        var wrong = new Movie
        {
            Id = id, Path = path, Name = "Avatar", OriginalTitle = "Avatar: The Way of Water",
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "76600", ["Imdb"] = "tt1630029" },
            ImageInfos = [new ItemImageInfo { Path = "wrong-poster.jpg", Type = ImageType.Primary }],
            Tagline = "Return to Pandora", CommunityRating = 7.6f, Tags = ["alien planet"],
        };
        var other = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(_temp, "regular", "Avatar.mkv"), Name = "Avatar", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "19995" } };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { wrong, other });
        library.Setup(l => l.GetPeople(wrong)).Returns(new[] { new PersonInfo { Name = "James Cameron" } });
        library.SetupGet(l => l.RootFolder).Returns(new AggregateFolder());
        library.Setup(l => l.UpdatePeopleAsync(It.IsAny<BaseItem>(), It.IsAny<IReadOnlyList<PersonInfo>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        library.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // Act
        var repaired = await new CatalogueMetadataRepair(library.Object).RepairAsync(candidate, path, Path.Combine(_temp, "backup"), CancellationToken.None);
        // Assert
        Assert.True(repaired);
        Assert.Equal(id, wrong.Id);
        Assert.Equal("Tatort: Avatar", wrong.Name);
        Assert.True(wrong.IsLocked);
        Assert.False(wrong.ProviderIds.ContainsKey("Tmdb"));
        Assert.False(wrong.ProviderIds.ContainsKey("Imdb"));
        Assert.Empty(wrong.ImageInfos);
        Assert.Empty(wrong.Tags);
        Assert.Null(wrong.Tagline);
        Assert.Null(wrong.CommunityRating);
        Assert.Equal("19995", other.ProviderIds["Tmdb"]);
        Assert.Contains("76600", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_temp, "backup")))));
        library.Verify(l => l.UpdatePeopleAsync(wrong, It.Is<IReadOnlyList<PersonInfo>>(p => p.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    private CatalogueCandidate Candidate(string title, string topic) => CataloguePolicy.Classify(new ResultItemDto
    {
        Id = Guid.NewGuid().ToString("N"), Title = title, Topic = topic, Channel = "ARD", Description = "Fernsehfilm Deutschland 2024 Eine besondere Geschichte über eine Familie und ihre gemeinsamen Abenteuer in einer kleinen Stadt.",
        Duration = TimeSpan.FromMinutes(90), Timestamp = DateTimeOffset.UtcNow,
        VideoUrls = [new VideoUrlDto { Url = "https://example.ard.de/" + Guid.NewGuid().ToString("N") + ".mp4", Quality = 3 }], SubtitleUrls = [], ExternalIds = [],
    }, _parser, true)!;

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, true);
        }
    }
}
