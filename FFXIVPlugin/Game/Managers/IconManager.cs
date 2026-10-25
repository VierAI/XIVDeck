using System.Collections.Generic;
using System.IO;
using Lumina.Data.Files;
using XIVDeck.FFXIVPlugin.Base;

namespace XIVDeck.FFXIVPlugin.Game.Managers;

// borrowed from https://github.com/Caraxi/RemindMe/blob/master/IconManager.cs
public static class IconManager {
    private const string IconFileFormat = "ui/icon/{0:D3}000/{1}{2:D6}{3}.tex";

    private static string GetIconPath(string lang, int iconId, bool highres = false) {
        var useHqIcon = false;

        if (iconId > 1_000_000) {
            useHqIcon = true;
            iconId -= 1_000_000;
        }

        var path = string.Format(IconFileFormat,
            iconId / 1000, (useHqIcon ? "hq/" : "") + lang, iconId, highres ? "_hr1" : "");

        return Injections.TextureSubstitutionProvider.GetSubstitutedPath(path);
    }

    /// <summary>
    /// The path GetIcon tries first for this icon, after texture substitution (e.g. Penumbra). Used to notice
    /// when an icon mod changes so cached PNGs can be dropped.
    /// </summary>
    public static string GetSubstitutedIconPath(int iconId) {
        return GetIconPath(string.Empty, iconId, true);
    }

    /// <summary>
    /// The substituted paths GetIcon("", iconId, true) would try, in order (high-res, then low-res).
    /// Texture substitution calls into other plugins (Penumbra), so call this from the framework thread.
    /// </summary>
    public static IReadOnlyList<string> GetSubstitutedIconPaths(int iconId) {
        var highRes = GetIconPath(string.Empty, iconId, true);
        var lowRes = GetIconPath(string.Empty, iconId);
        return highRes == lowRes ? [highRes] : [highRes, lowRes];
    }

    /// <summary>
    /// Reads an already-substituted tex path (game data or an on-disk mod file). Safe off the framework thread.
    /// </summary>
    public static TexFile? LoadTex(string texPath) {
        try {
            return Path.IsPathRooted(texPath)
                ? Injections.DataManager.GameData.GetFileFromDisk<TexFile>(texPath)
                : Injections.DataManager.GetFile<TexFile>(texPath);
        } catch (FileNotFoundException) {
            return null;
        }
    }
    public static TexFile? GetIcon(string lang, int iconId, bool highres = false) {
        TexFile? texFile;

        if (lang.Length > 0 && !lang.EndsWith('/'))
            lang += "/";

        var texPath = GetIconPath(lang, iconId, true);

        if (Path.IsPathRooted(texPath)) {
            Injections.PluginLog.Verbose($"Using on-disk asset {texPath}");
            texFile = Injections.DataManager.GameData.GetFileFromDisk<TexFile>(texPath);
        } else {
            texFile = Injections.DataManager.GetFile<TexFile>(texPath);
        }

        // recursion steps:
        // - attempt to get the high-res file exactly as described.
        //   - attempt to get the high-res file without language
        //     - attempt to get the low-res file without language
        //   - attempt to get the low-res file exactly as described
        //     - attempt to get the low-res file without language
        // - give up and return null
        switch (texFile) {
            case null when lang.Length > 0:
                Injections.PluginLog.Debug($"Couldn't get lang-specific icon for {texPath}, falling back to no-lang");
                return GetIcon(string.Empty, iconId, true);
            case null when highres:
                Injections.PluginLog.Debug($"Couldn't get highres icon for {texPath}, falling back to lowres");
                return GetIcon(lang, iconId);
            default:
                return texFile;
        }
    }
}
