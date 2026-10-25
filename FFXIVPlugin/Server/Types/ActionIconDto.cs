using Newtonsoft.Json;

namespace XIVDeck.FFXIVPlugin.Server.Types;

public class ActionIconDto {
    [JsonProperty("type")] public string Type;
    [JsonProperty("id")] public int Id;
    [JsonProperty("iconId")] public int IconId;

    /// <summary>
    /// Display name, sent for player-editable entries (macros, gearsets) so the deck can refresh its saved copy.
    /// </summary>
    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string? Name;

    public ActionIconDto(string type, int id, int iconId, string? name = null) {
        this.Type = type;
        this.Id = id;
        this.IconId = iconId;
        this.Name = name;
    }
}
