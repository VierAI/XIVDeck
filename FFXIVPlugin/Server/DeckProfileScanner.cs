using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using XIVDeck.FFXIVPlugin.Base;

namespace XIVDeck.FFXIVPlugin.Server;

/// <summary>
/// Finds every XIVDeck key in every Stream Deck profile and page, so their icons can be pushed to the deck before
/// the page is ever shown. The Stream Deck SDK only tells a plugin about keys on screen (willAppear), and the deck
/// half runs in Stream Deck's browser sandbox (CodePath plugin.html) with no file access, so the game plugin reads
/// the profile manifests instead. Read-only; files are opened with full sharing so Stream Deck is never blocked.
///
/// Layout (Stream Deck 6.x/7.x): %APPDATA%\Elgato\StreamDeck\ProfilesV3\{profile}.sdProfile\manifest.json and
/// ...\Profiles\{page}\manifest.json, each with Controllers[].Actions{"col,row": {UUID, Settings, ...}}. Multi-actions
/// and folders nest further action objects, so the scan walks the whole JSON tree.
///
/// Hotbar keys are skipped: the game already pushes every hotbar slot (18 x 16) and its icon on connect.
/// </summary>
internal sealed class DeckProfileScanner : IDisposable {
    private const string ActionPrefix = "dev.wolf.xivdeck.sdplugin.actions.";
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Timer _timer;
    private string _signature = "";
    private IReadOnlyList<(string Type, int Id)> _actions = [];

    public DeckProfileScanner() {
        // first check soon after load, then periodically; only a changed set of files triggers a re-parse
        this._timer = new Timer(_ => this.CheckForChanges(), null, TimeSpan.FromSeconds(2), RescanInterval);
    }

    /// <summary>
    /// Every action/macro/class key found in the profiles (deduplicated). Re-reads the files if they changed.
    /// </summary>
    public IReadOnlyList<(string Type, int Id)> GetActions() {
        lock (this._gate) {
            this.RescanIfChanged(out _);
            return this._actions;
        }
    }

    private void CheckForChanges() {
        try {
            List<(string Type, int Id)> added;
            lock (this._gate) {
                if (!this.RescanIfChanged(out added)) return;
            }

            // profile edited (key added/changed, profile imported): warm the new entries on connected decks
            if (added.Count > 0)
                XIVDeckPlugin.Instance?.Server?.Prefetch(null, added);
        } catch (Exception ex) {
            Injections.PluginLog.Debug(ex, "Stream Deck profile rescan failed");
        }
    }

    /// <returns>True if the files changed and were re-read; <paramref name="added"/> holds entries not seen before.</returns>
    private bool RescanIfChanged(out List<(string Type, int Id)> added) {
        added = [];

        var manifests = FindManifests();
        var signature = BuildSignature(manifests);
        if (signature == this._signature) return false;

        var previous = this._actions.ToHashSet();
        var found = new HashSet<(string Type, int Id)>();
        var keyCount = 0;

        foreach (var file in manifests) {
            try {
                var json = ReadShared(file.FullName);
                Collect(JToken.Parse(json), found, ref keyCount);
            } catch (Exception ex) {
                Injections.PluginLog.Debug(ex, $"Couldn't read Stream Deck profile {file.FullName}");
            }
        }

        this._signature = signature;
        this._actions = found.ToList();
        added = found.Where(action => !previous.Contains(action)).ToList();

        Injections.PluginLog.Debug(
            $"Stream Deck profiles: {manifests.Count} manifest(s), {keyCount} XIVDeck key(s), " +
            $"{found.Count} distinct non-hotbar icon target(s), {added.Count} new");
        return true;
    }

    private static List<FileInfo> FindManifests() {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck");
        var result = new List<FileInfo>();

        foreach (var dir in new[] { "ProfilesV3", "ProfilesV2" }) {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            try {
                result.AddRange(new DirectoryInfo(path).EnumerateFiles("manifest.json", SearchOption.AllDirectories));
            } catch (Exception ex) {
                Injections.PluginLog.Debug(ex, $"Couldn't list {path}");
            }
        }

        return result;
    }

    private static string BuildSignature(List<FileInfo> files) {
        var sb = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)) {
            file.Refresh();
            if (!file.Exists) continue;
            sb.Append(file.FullName).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|').Append(file.Length).Append(';');
        }

        return sb.ToString();
    }

    private static string ReadShared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Collect(JToken token, HashSet<(string Type, int Id)> found, ref int keyCount) {
        switch (token) {
            case JObject obj: {
                if (obj.Value<string>("UUID") is { } uuid &&
                    uuid.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase) &&
                    obj["Settings"] is JObject settings) {
                    keyCount++;
                    if (TryTarget(uuid[ActionPrefix.Length..].ToLowerInvariant(), settings, out var target))
                        found.Add(target);
                }

                foreach (var property in obj.Properties())
                    Collect(property.Value, found, ref keyCount);
                break;
            }
            case JArray array:
                foreach (var item in array)
                    Collect(item, found, ref keyCount);
                break;
        }
    }

    /// <summary>Same type/id the deck buttons subscribe with (ActionButton, MacroButton, ClassButton).</summary>
    private static bool TryTarget(string action, JObject settings, out (string Type, int Id) target) {
        target = default;

        switch (action) {
            case "execaction": {
                var type = settings.Value<string>("actionType");
                var id = ReadInt(settings["actionId"]);
                if (string.IsNullOrEmpty(type) || id == null) return false;
                if (type == "Collection") type = "McGuffin"; // same migration as ActionButton
                target = (type, id.Value);
                return true;
            }
            case "execmacro": {
                var id = ReadInt(settings["macroId"]);
                if (id == null) return false;
                target = ("Macro", id.Value);
                return true;
            }
            case "switchclass": {
                var id = ReadInt(settings["classId"]);
                if (id == null) return false;
                target = ("Class", id.Value);
                return true;
            }
            default:
                return false; // exechotbar is covered by the full hotbar push; command/volume keys have no game icon
        }
    }

    private static int? ReadInt(JToken? token) {
        if (token == null) return null;
        return token.Type switch {
            JTokenType.Integer => token.Value<int>(),
            JTokenType.Float => (int)token.Value<double>(),
            JTokenType.String when int.TryParse(token.Value<string>(), out var parsed) => parsed,
            _ => null
        };
    }

    public void Dispose() {
        this._timer.Dispose();
    }
}
