using Newtonsoft.Json;

namespace XIVDeck.FFXIVPlugin.Server.Messages.Outbound;

public class WSIconMessage : BaseOutboundMessage {
    private const string MessageName = "icon";

    [JsonProperty("iconId")] public int IconId { get; }
    [JsonProperty("dataUrl")] public string DataUrl { get; }

    public WSIconMessage(int iconId, string dataUrl) : base(MessageName) {
        this.IconId = iconId;
        this.DataUrl = dataUrl;
    }
}
