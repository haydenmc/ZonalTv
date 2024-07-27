using System.Text.Json.Serialization;

namespace ZonalTv.Data;

public enum WebSocketMessageKind
{
    Watch = 1,
    Unwatch = 2,
}

public class WebSocketMessage
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WebSocketMessageKind Type;

    [JsonPropertyName("channelId")]
    public ulong? ChannelId;
}