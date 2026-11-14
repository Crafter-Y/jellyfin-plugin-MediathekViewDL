using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Catalogue;

/// <summary>Maintains only catalogue-owned links, metadata, and recoverable retirement records.</summary>
public sealed class CatalogueStore
{
    private const string Owner = "MediathekViewDL automatic catalogue v1";
    private readonly string _root;
    private readonly string _statePath;
    private readonly string _journalPath;
    private readonly string _retiredPath;

    /// <summary>Initializes a new instance of the <see cref="CatalogueStore"/> class in a dedicated directory.</summary>
    /// <param name="root">Dedicated library root.</param>
    /// <param name="dataPath">Private plugin state directory, outside the library root.</param>
    public CatalogueStore(string root, string dataPath)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("Choose an absolute, dedicated catalogue directory.", nameof(root));
        }

        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(_root, Path.GetPathRoot(_root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A filesystem root cannot be used as the catalogue directory.", nameof(root));
        }

        EnsureNoLinks(_root);
        var stateRoot = Path.GetFullPath(dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (stateRoot.Equals(_root, comparison) || stateRoot.StartsWith(_root + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("Private state must be outside the library root.", nameof(dataPath));
        }

        EnsureNoLinks(stateRoot);
        var marker = Path.Combine(_root, ".mediathek-catalogue");
        EnsureNoLinks(marker);
        if (Directory.Exists(_root) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(_root).Any())
        {
            throw new IOException("The catalogue root must be empty or already managed by this feature.");
        }

        Directory.CreateDirectory(_root);
        if (!File.Exists(marker))
        {
            using var file = new StreamWriter(new FileStream(marker, FileMode.CreateNew, FileAccess.Write));
            file.Write(Owner);
        }

        if (File.ReadAllText(marker) != Owner)
        {
            throw new IOException("Catalogue ownership marker does not match.");
        }

        Directory.CreateDirectory(dataPath);
        _statePath = Path.Combine(dataPath, "catalogue-state.json");
        _journalPath = Path.Combine(dataPath, "catalogue-journal.jsonl");
        _retiredPath = Path.Combine(dataPath, "retired");
        State = File.Exists(_statePath)
            ? JsonSerializer.Deserialize<CatalogueState>(File.ReadAllText(_statePath)) ?? throw new IOException("Invalid catalogue state")
            : new CatalogueState { Root = _root };
        if (!string.Equals(State.Root, _root, StringComparison.Ordinal))
        {
            throw new IOException("Catalogue root changed. Move or reset the catalogue state explicitly before migration.");
        }

        ReplayJournal();
        Save();
    }

    /// <summary>Gets the last durably recorded sync state.</summary>
    public CatalogueState State { get; }

    /// <summary>Writes or refreshes a link without overwriting user-modified files.</summary>
    /// <param name="item">The identified programme.</param>
    /// <param name="url">Selected broadcaster stream URL.</param>
    /// <param name="now">Observation time.</param>
    /// <returns>Whether this item was safely managed.</returns>
    public bool Upsert(CatalogueCandidate item, string url, DateTimeOffset now)
    {
        State.Entries.TryGetValue(item.Key, out var entry);
        var show = item.Season.HasValue;
        var suffix = item.Year.HasValue ? FormattableString.Invariant($" ({item.Year})") : string.Empty;
        var folder = show ? CataloguePolicy.SafeName(item.Topic) : CataloguePolicy.SafeName(item.Title + suffix);
        var filename = show ? FormattableString.Invariant($"S{item.Season:00}E{item.Episode:00} - {item.Title}") : item.Title + suffix;
        var relative = entry?.RelativePath ?? Path.Combine(
            show ? "Shows" : "Movies",
            folder,
            show ? FormattableString.Invariant($"Season {item.Season:00}") : string.Empty,
            CataloguePolicy.SafeName(filename) + "." + item.Language + "." + item.Key[..10] + ".strm");
        var path = Resolve(relative);
        var nfoPath = Path.ChangeExtension(path, ".nfo");
        if (entry is not null && !Unchanged(entry))
        {
            return false;
        }

        var strm = url + "\n# " + Owner + " " + item.Key + "\n";
        var nfo = new XElement(
            show ? "episodedetails" : "movie",
            new XElement("title", item.Title),
            new XElement("plot", item.Source.Description),
            new XElement("uniqueid", new XAttribute("type", "mediathek-catalogue"), item.Key));
        if (item.Year.HasValue)
        {
            nfo.Add(new XElement("year", item.Year.Value));
        }

        if (show)
        {
            nfo.Add(new XElement("showtitle", item.Topic), new XElement("season", item.Season), new XElement("episode", item.Episode));
        }

        if (item.UseSourceMetadata)
        {
            nfo.Add(
                new XElement("lockdata", true),
                new XElement("originaltitle", item.Title),
                new XElement("studio", item.Source.Channel),
                new XElement("runtime", (int)item.Source.Duration.TotalMinutes));
        }

        foreach (var id in item.Source.ExternalIds.Where(id => id.Source is "imdb" or "tmdb" or "tvdb"))
        {
            nfo.Add(new XElement("uniqueid", new XAttribute("type", id.Source), id.Id));
        }

        // A crash can occur after the two atomic file writes but before the journal
        // append. Reclaim only an exact pair of our expected bytes; never an edit.
        if (entry is null && (File.Exists(path) || File.Exists(nfoPath))
            && (!File.Exists(path) || !File.Exists(nfoPath) || File.ReadAllText(path) != strm || File.ReadAllText(nfoPath) != nfo.ToString()))
        {
            return false;
        }

        if (entry is not null && File.Exists(path) && File.Exists(nfoPath)
            && File.ReadAllText(path) == strm && File.ReadAllText(nfoPath) == nfo.ToString())
        {
            entry.LastSeen = now;
            entry.SourceId = item.Source.Id;
            entry.SourceTitle = item.Source.Title;
            entry.SourceTopic = item.Source.Topic;
            entry.SourceDescription = item.Source.Description;
            entry.DuplicateKey = item.DuplicateKey;
            MarkAvailable(item.Key);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, strm);
        AtomicWrite(nfoPath, nfo.ToString());
        State.Entries[item.Key] = new CatalogueEntry
        {
            RelativePath = relative, Url = url, LinkHash = HashFile(path), NfoHash = HashFile(nfoPath),
            LastSeen = now, MissingSince = null, MissingChecks = 0,
            MetadataGuardVersion = entry?.MetadataGuardVersion ?? 0,
            SourceId = item.Source.Id, SourceTitle = item.Source.Title, SourceTopic = item.Source.Topic,
            SourceDescription = item.Source.Description,
            DuplicateKey = item.DuplicateKey,
        };
        AppendMutation(item.Key, State.Entries[item.Key]);
        return true;
    }

    /// <summary>Returns an owned, unmodified link path, or null for protected/unmanaged files.</summary>
    /// <param name="key">Managed identity.</param>
    /// <returns>The absolute link path.</returns>
    public string? GetOwnedPath(string key)
        => State.Entries.TryGetValue(key, out var entry) && Unchanged(entry) ? Resolve(entry.RelativePath) : null;

    /// <summary>Durably records a successful Jellyfin metadata repair.</summary>
    /// <param name="key">Managed identity.</param>
    public void MarkMetadataRepaired(string key)
    {
        State.Entries[key].MetadataGuardVersion = 3;
        AppendMutation(key, State.Entries[key]);
    }

    /// <summary>Archives only verified owned files for a confidently identified duplicate.</summary>
    /// <param name="key">Duplicate identity.</param>
    /// <param name="now">Archive time.</param>
    /// <returns>Whether the duplicate was archived.</returns>
    public bool ArchiveDuplicate(string key, DateTimeOffset now)
    {
        if (!State.Entries.TryGetValue(key, out var entry) || !Unchanged(entry))
        {
            return false;
        }

        var directory = Path.Combine(_retiredPath, "duplicates", now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), key);
        Directory.CreateDirectory(directory);
        foreach (var path in new[] { Resolve(entry.RelativePath), Path.ChangeExtension(Resolve(entry.RelativePath), ".nfo") })
        {
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(directory, Path.GetFileName(path)), false);
            }
        }

        State.Entries.Remove(key);
        AppendMutation(key, null);
        return true;
    }

    /// <summary>Records a definite 404/410, retiring only after repeated checks and a grace period.</summary>
    /// <param name="key">Managed item key.</param>
    /// <param name="now">Check time.</param>
    /// <param name="graceHours">Minimum grace interval.</param>
    /// <returns>Whether the managed files were moved out of the library.</returns>
    public bool MarkUnavailable(string key, DateTimeOffset now, int graceHours)
    {
        var entry = State.Entries[key];
        entry.MissingSince ??= now;
        if (!entry.LastFailure.HasValue || now - entry.LastFailure.Value >= TimeSpan.FromHours(6))
        {
            entry.MissingChecks++;
            entry.LastFailure = now;
        }

        var retired = entry.MissingChecks >= 3 && now - entry.MissingSince.Value >= TimeSpan.FromHours(Math.Max(24, graceHours)) && Unchanged(entry);
        if (retired)
        {
            var directory = Path.Combine(_retiredPath, now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), key);
            Directory.CreateDirectory(directory);
            foreach (var path in new[] { Resolve(entry.RelativePath), Path.ChangeExtension(Resolve(entry.RelativePath), ".nfo") })
            {
                if (File.Exists(path))
                {
                    File.Move(path, Path.Combine(directory, Path.GetFileName(path)), false);
                }
            }

            State.Entries.Remove(key);
        }

        AppendMutation(key, retired ? null : entry);
        return retired;
    }

    /// <summary>Records a valid stream, cancelling any pending retirement.</summary>
    /// <param name="key">Managed item key.</param>
    public void MarkAvailable(string key)
    {
        var entry = State.Entries[key];
        entry.MissingSince = null;
        entry.LastFailure = null;
        entry.MissingChecks = 0;
    }

    /// <summary>Atomically persists the current state.</summary>
    public void Save()
    {
        // Commit the new generation before clearing the old journal. A crash between
        // these writes cannot replay stale mutations over the committed checkpoint.
        State.JournalGeneration = Guid.NewGuid();
        AtomicWrite(_statePath, JsonSerializer.Serialize(State));
        AtomicWrite(_journalPath, string.Empty);
    }

    private void AppendMutation(string key, CatalogueEntry? entry)
        => File.AppendAllText(_journalPath, JsonSerializer.Serialize(new CatalogueMutation(State.JournalGeneration, key, entry)) + "\n", new UTF8Encoding(false));

    private void ReplayJournal()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        var lines = File.ReadAllLines(_journalPath);
        for (var index = 0; index < lines.Length; index++)
        {
            CatalogueMutation? change;
            try
            {
                change = JsonSerializer.Deserialize<CatalogueMutation>(lines[index]);
            }
            catch (JsonException) when (index == lines.Length - 1)
            {
                // A process crash may leave only its final append incomplete.
                break;
            }

            if (change is null || change.Generation != State.JournalGeneration)
            {
                continue;
            }

            if (change.Entry is null)
            {
                State.Entries.Remove(change.Key);
            }
            else
            {
                State.Entries[change.Key] = change.Entry;
            }
        }
    }

    private bool Unchanged(CatalogueEntry entry)
    {
        var path = Resolve(entry.RelativePath);
        var nfo = Path.ChangeExtension(path, ".nfo");
        return (!File.Exists(path) || HashFile(path) == entry.LinkHash)
            && (!File.Exists(nfo) || HashFile(nfo) == entry.NfoHash);
    }

    private string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, comparison) || Path.IsPathRooted(relative) || Path.GetExtension(path) != ".strm")
        {
            throw new IOException("Unsafe catalogue file path.");
        }

        EnsureNoLinks(path);
        EnsureNoLinks(Path.ChangeExtension(path, ".nfo"));
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Symlinks/reparse points are not allowed in managed catalogue paths.");
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
        {
            writer.Write(content);
        }

        File.Move(temporary, path, true);
    }
}
