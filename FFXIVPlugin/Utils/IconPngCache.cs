using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XIVDeck.FFXIVPlugin.Base;
using XIVDeck.FFXIVPlugin.Game.Managers;

namespace XIVDeck.FFXIVPlugin.Utils;

/// <summary>
/// One PNG encode per icon id. Callers must not invoke this from the framework thread.
/// A failed lookup is not cached, so a tex that appears later can still be encoded.
/// Each entry remembers the substituted tex path it was encoded from, so an icon mod (Penumbra) that changes
/// the path can be detected with <see cref="FindSubstitutionChanges"/> and the entry evicted.
/// Texture substitution (Penumbra) is resolved on the framework thread; only the tex read and the PNG encode
/// run on the thread pool. The cache is an LRU capped at <see cref="MaxEntries"/> and keeps each PNG once
/// (as bytes); data: URLs are built from those bytes on demand.
/// </summary>
public static class IconPngCache {
    private const int MaxEntries = 1024;

    private sealed record Entry(int IconId, byte[] Png, string? SourcePath);

    private static readonly object Sync = new();
    private static readonly Dictionary<int, LinkedListNode<Entry>> Index = new();
    private static readonly LinkedList<Entry> Lru = new();
    private static readonly ConcurrentDictionary<int, Lazy<Task<byte[]?>>> InFlight = new();

    public static bool IsEmpty {
        get { lock (Sync) return Index.Count == 0; }
    }

    private static bool TryGet(int iconId, out byte[] png) {
        lock (Sync) {
            if (Index.TryGetValue(iconId, out var node)) {
                Lru.Remove(node);
                Lru.AddFirst(node);
                png = node.Value.Png;
                return true;
            }
        }

        png = [];
        return false;
    }

    private static void Put(Entry entry) {
        lock (Sync) {
            if (Index.TryGetValue(entry.IconId, out var old)) {
                Lru.Remove(old);
                Index.Remove(entry.IconId);
            }

            Index[entry.IconId] = Lru.AddFirst(entry);
            while (Index.Count > MaxEntries && Lru.Last != null) {
                Index.Remove(Lru.Last.Value.IconId);
                Lru.RemoveLast();
            }
        }
    }

    public static async Task<byte[]?> GetBytesAsync(int iconId) {
        if (iconId <= 0) return null;
        if (TryGet(iconId, out var hit)) return hit;

        // One encode per icon at a time; the in-flight entry is dropped as soon as it finishes.
        var lazy = InFlight.GetOrAdd(iconId, id => new Lazy<Task<byte[]?>>(() => EncodeAndStore(id)));
        try {
            return await lazy.Value;
        } finally {
            InFlight.TryRemove(new KeyValuePair<int, Lazy<Task<byte[]?>>>(iconId, lazy));
        }
    }

    private static async Task<byte[]?> EncodeAndStore(int iconId) {
        if (TryGet(iconId, out var hit)) return hit;

        IReadOnlyList<string> paths;
        try {
            paths = await Injections.Framework.RunOnFrameworkThread(() => IconManager.GetSubstitutedIconPaths(iconId));
        } catch (Exception ex) {
            Injections.PluginLog.Warning(ex, $"Couldn't resolve icon path for {iconId}");
            return null;
        }

        var encoded = await Task.Run(() => Encode(iconId, paths));
        if (encoded == null) return null;

        Put(new Entry(iconId, encoded, paths.Count > 0 ? paths[0] : null));
        return encoded;
    }

    public static async Task<string?> GetDataUrlAsync(int iconId) {
        var bytes = await GetBytesAsync(iconId);
        return bytes == null ? null : "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    public static async Task<Dictionary<int, string>> GetDataUrlsAsync(IEnumerable<int> iconIds) {
        var pending = new List<Task<(int Id, string? Url)>>();
        var seen = new HashSet<int>();

        foreach (var iconId in iconIds) {
            if (iconId <= 0 || !seen.Add(iconId)) continue;
            pending.Add(Load(iconId));
        }

        var result = new Dictionary<int, string>();
        foreach (var item in await Task.WhenAll(pending)) {
            if (item.Url != null) result[item.Id] = item.Url;
        }

        return result;

        static async Task<(int Id, string? Url)> Load(int id) => (id, await GetDataUrlAsync(id));
    }

    /// <summary>
    /// Drop specific icons so the next request re-reads the tex (and any mod replacing it).
    /// </summary>
    public static void Evict(IEnumerable<int> iconIds) {
        lock (Sync) {
            foreach (var iconId in iconIds) {
                if (Index.Remove(iconId, out var node)) Lru.Remove(node);
            }
        }
    }

    /// <summary>
    /// Drop every cached PNG. Used by the debug "Redraw All Icons" button.
    /// </summary>
    public static void Clear() {
        lock (Sync) {
            Index.Clear();
            Lru.Clear();
        }
    }

    /// <summary>
    /// Re-resolve the substituted tex path of every cached icon and return the ids whose path changed since they
    /// were encoded, e.g. because a Penumbra mod touching ui/icon was enabled, disabled or switched.
    /// Cheap (a path lookup per cached icon, no file reads). Call from the framework thread.
    /// </summary>
    public static List<int> FindSubstitutionChanges() {
        var changed = new List<int>();
        (int Id, string Path)[] snapshot;
        lock (Sync) {
            snapshot = Lru.Where(e => e.SourcePath != null).Select(e => (e.IconId, e.SourcePath!)).ToArray();
        }

        foreach (var (iconId, oldPath) in snapshot) {
            string current;
            try {
                current = IconManager.GetSubstitutedIconPath(iconId);
            } catch (Exception ex) {
                Injections.PluginLog.Verbose(ex, $"Couldn't re-resolve icon path for {iconId}");
                continue;
            }

            if (!string.Equals(current, oldPath, StringComparison.OrdinalIgnoreCase))
                changed.Add(iconId);
        }

        return changed;
    }

    // Thread-pool side: reads the already-substituted paths in order and encodes the first that exists.
    private static byte[]? Encode(int iconId, IReadOnlyList<string> paths) {
        try {
            foreach (var path in paths) {
                var tex = IconManager.LoadTex(path);
                if (tex == null) continue;

                using var image = tex.GetImage();
                return image.ConvertToPng();
            }

            return null;
        } catch (Exception ex) {
            Injections.PluginLog.Warning(ex, $"Couldn't encode icon {iconId}");
            return null;
        }
    }
}