using Newtonsoft.Json;

namespace XIVDeck.FFXIVPlugin.Server.Messages.Outbound;

/**
 * A WebSocket message to disclose a generic update of state, generally for actions or some other case where it
 * would be a good idea for the Stream Deck plugin to refresh (a subset of) information.
 */
public class WSInitReplyMessage : BaseOutboundMessage {
    private const string MessageName = "initReply";

    [JsonProperty("version")] public string Version { get; set; }
    [JsonProperty("apiKey")] public string? ApiKey { get; set; }

    /// <summary>
    /// True when this game plugin pushes hotbar slots and icon PNGs. Older Stream Deck builds ignore it.
    /// </summary>
    [JsonProperty("pushCache")] public bool PushCache { get; set; }

    public WSInitReplyMessage(string version, string? apiKey = null, bool pushCache = false) : base(MessageName) {
        this.Version = version;
        this.ApiKey = apiKey;
        this.PushCache = pushCache;
    }
}
