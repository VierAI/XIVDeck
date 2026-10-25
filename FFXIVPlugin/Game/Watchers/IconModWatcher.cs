using System;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using XIVDeck.FFXIVPlugin.Base;
using XIVDeck.FFXIVPlugin.Utils;

namespace XIVDeck.FFXIVPlugin.Game.Watchers;

/// <summary>
/// Notices icon mods (Penumbra texture substitution) changing under cached PNGs. Every few seconds it re-resolves
/// the substituted tex path of each cached icon; any that moved are evicted and re-sent to decks that had them.
/// Dalamud has no "substitution changed" event for plugins, so this polls; it's a dictionary lookup per icon.
/// </summary>
public class IconModWatcher : IDisposable {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Stopwatch _sinceCheck = Stopwatch.StartNew();

    public IconModWatcher() {
        Injections.Framework.Update += this.OnUpdate;
    }

    private void OnUpdate(IFramework framework) {
        if (this._sinceCheck.Elapsed < Interval) return;
        this._sinceCheck.Restart();

        if (IconPngCache.IsEmpty) return;

        try {
            var changed = IconPngCache.FindSubstitutionChanges();
            if (changed.Count == 0) return;

            Injections.PluginLog.Debug($"{changed.Count} cached icon(s) changed texture source; refreshing Stream Deck");
            IconPngCache.Evict(changed);
            XIVDeckPlugin.Instance?.Server?.ReplaceIcons(changed);
        } catch (Exception ex) {
            Injections.PluginLog.Warning(ex, "Icon mod check failed");
        }
    }

    public void Dispose() {
        Injections.Framework.Update -= this.OnUpdate;
        GC.SuppressFinalize(this);
    }
}
