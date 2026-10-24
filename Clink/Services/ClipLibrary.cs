using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clink.Services;

internal sealed class ClipEntry
{
    public string Path { get; set; } = "";
    public DateTime Created { get; set; }
    public double Duration { get; set; }
    public long Size { get; set; }
    public string? Game { get; set; }
    public string[]? AudioTracks { get; set; }
    public string? CopiumLink { get; set; }
    public string? CopiumPageUrl { get; set; }
    public string? CopiumId { get; set; }
    /// <summary>Made by the trim editor (from <see cref="SourcePath"/>).</summary>
    public bool IsTrim { get; set; }
    public string? SourcePath { get; set; }
    /// <summary>Set when the archive service moved the file to the archive location (<see cref="Path"/> then points there).</summary>
    public DateTime? ArchivedAt { get; set; }
    /// <summary>A manual recording, not a clip (hours long): "share last" and "trim last" skip it. Missing in older indexes: false.</summary>
    public bool IsRecording { get; set; }

    [JsonIgnore] public bool IsShared => !string.IsNullOrEmpty(CopiumLink);
    [JsonIgnore] public bool IsArchived => ArchivedAt != null;

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public bool Exists => File.Exists(Path);
}

/// <summary>How <see cref="ClipLibrary.MoveToArchive"/> ended.</summary>
internal enum ArchiveMove
{
    /// <summary>The local file is deleted and the entry points at the archive.</summary>
    Moved,
    /// <summary>The clip left the library meanwhile: nothing was deleted or changed.</summary>
    NotInLibrary,
    /// <summary>The delete did not happen (the caller says why): nothing was changed.</summary>
    NotDeleted,
}

/// <summary>Index of clips (sidecar JSON next to the settings) merged with whatever is in the clips folder.</summary>
internal sealed class ClipLibrary
{
    private readonly object _gate = new();
    private readonly List<ClipEntry> _entries = new();
    private static string IndexPath => IndexPathOverride ?? System.IO.Path.Combine(Settings.Dir, "clips.json");
    /// <summary>Where the index lives; set by test harnesses so they never touch the real clips.json.</summary>
    internal static string? IndexPathOverride { get; set; }
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private bool _savesFrozen;

    public event Action? Changed;

    /// <summary>
    /// From now on this library writes nothing to the index (the entries still change in memory). Waits for a write that is
    /// under way first, so when it returns clips.json will not be written by this process again: a restart that has just merged
    /// another copy's clips into it (<c>SettingsImport</c>) is not undone by a clip saved, shared or archived a moment later.
    /// </summary>
    public void FreezeSaves() { lock (_gate) _savesFrozen = true; }

    /// <summary>Undoes <see cref="FreezeSaves"/> (a restart that did not happen).</summary>
    public void ThawSaves() { lock (_gate) _savesFrozen = false; }

    internal bool SavesFrozen { get { lock (_gate) return _savesFrozen; } }

    public void Load(string clipsFolder)
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                // The index is the only record of archived clips and of shared ones whose local file is gone: a damaged
                // clips.json (crash mid-write) falls back to the previous version kept beside it.
                var loaded = AtomicFile.ReadWithFallback(IndexPath, json => JsonSerializer.Deserialize<List<ClipEntry>>(json, s_json));
                if (loaded != null) _entries.AddRange(loaded.Where(e => e != null));
            }
            catch (Exception ex) { Log.Warn("clips.json: " + ex.Message); }
            // A deleted local file drops its entry, except: shared clips stay (the Cloud tab lists them by link), and archived
            // clips are not checked here at all, because File.Exists on an unreachable share can block for seconds at startup;
            // the library rows say "archive offline" instead.
            _entries.RemoveAll(e => string.IsNullOrEmpty(e.Path) || (!e.IsShared && !e.IsArchived && !File.Exists(e.Path)));
            // Pick up clips that were made elsewhere / before the index existed.
            try
            {
                if (Directory.Exists(clipsFolder))
                    foreach (var f in Directory.EnumerateFiles(clipsFolder, "*.mp4"))
                        if (!_entries.Any(e => string.Equals(e.Path, f, StringComparison.OrdinalIgnoreCase)))
                        {
                            var fi = new FileInfo(f);
                            _entries.Add(new ClipEntry { Path = f, Created = fi.CreationTime, Size = fi.Length });
                        }
            }
            catch (Exception ex) { Log.Warn("scan clips: " + ex.Message); }
            _entries.Sort((a, b) => b.Created.CompareTo(a.Created));
        }
        Changed?.Invoke();
    }

    /// <summary>Snapshot, newest first.</summary>
    public IReadOnlyList<ClipEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    /// <summary>The entry for a file path (case-insensitive), or null.</summary>
    public ClipEntry? Find(string path)
    {
        lock (_gate) return _entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="isTrim">Made by the trim editor; <paramref name="sourcePath"/> is the clip it was cut from.</param>
    public ClipEntry Add(string path, double duration, long size, string? game, string[] audioTracks, bool isTrim = false, string? sourcePath = null, bool isRecording = false)
    {
        var e = new ClipEntry
        {
            Path = path, Created = DateTime.Now, Duration = duration, Size = size, Game = game, AudioTracks = audioTracks,
            IsRecording = isRecording,
            IsTrim = isTrim, SourcePath = isTrim ? sourcePath : null,
        };
        lock (_gate)
        {
            _entries.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, e);
            SaveLocked();
        }
        Changed?.Invoke();
        return e;
    }

    public void Update(ClipEntry e)
    {
        lock (_gate) SaveLocked();
        Changed?.Invoke();
    }

    public void Remove(ClipEntry e)
    {
        lock (_gate) { _entries.Remove(e); SaveLocked(); }
        Changed?.Invoke();
    }

    /// <summary>
    /// The last step of an archive move, with the library locked so that a reload (<see cref="Load"/>) cannot run between the
    /// delete and the save and drop the entry of a clip whose local file is already gone: finds the entry for the clip that was at
    /// <paramref name="localPath"/> now (the snapshot the move began with may have been replaced by a reload), runs
    /// <paramref name="deleteLocal"/> and, when that returns true, points the entry (and the trims cut from it) at
    /// <paramref name="archivePath"/>, stamps it archived and saves. Nothing is deleted, and nothing changes, when the clip is no
    /// longer in the library or the delete does not happen. <paramref name="deleteLocal"/> runs under the lock: keep it short.
    /// </summary>
    public ArchiveMove MoveToArchive(ClipEntry snapshot, string localPath, string archivePath, Func<bool> deleteLocal)
    {
        lock (_gate)
        {
            var live = _entries.FirstOrDefault(e => SamePath(e.Path, snapshot.Path)) ?? _entries.FirstOrDefault(e => SamePath(e.Path, localPath));
            if (live == null) return ArchiveMove.NotInLibrary;
            if (!deleteLocal()) return ArchiveMove.NotDeleted;
            live.Path = archivePath;
            live.ArchivedAt = DateTime.Now;
            foreach (var other in _entries)
                if (!ReferenceEquals(other, live) && SamePath(other.SourcePath, localPath)) other.SourcePath = archivePath;
            SaveLocked();
        }
        Changed?.Invoke();
        return ArchiveMove.Moved;
    }

    private static bool SamePath(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void SaveLocked()
    {
        if (_savesFrozen) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(IndexPath) ?? Settings.Dir);
            AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(_entries, s_json));
        }
        catch (Exception ex) { Log.Warn("clips.json save: " + ex.Message); }
    }
}
