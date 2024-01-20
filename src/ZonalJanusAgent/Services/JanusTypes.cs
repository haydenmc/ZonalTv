using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZonalJanusAgent.Services;

public partial class JanusWebsocketClientService
{
    private class JanusJsonMessage : JsonNode
    {
        
    }
    private class JanusSession(ulong sessionId)
    {
        public readonly ulong SessionId = sessionId;
        public ulong VideoRoomHandle;
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "janus",
        IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(JanusCreateMessage), "create")]
    [JsonDerivedType(typeof(JanusKeepAliveMessage), "keepalive")]
    [JsonDerivedType(typeof(JanusAckMessage), "ack")]
    [JsonDerivedType(typeof(JanusAttachMessage), "attach")]
    [JsonDerivedType(typeof(JanusSuccessMessage), "success")]
    [JsonDerivedType(typeof(JanusPluginMessage), "message")]
    private class JanusMessage
    {
        [JsonPropertyName("janus")]
        public virtual string Command { get; set; } = default!;

        [JsonPropertyName("transaction")]
        public string TransactionId { get; set; } = Path.GetRandomFileName();

        [JsonPropertyName("session_id")]
        public ulong? SessionId { get; set; } = null;
    }

    private class JanusKeepAliveMessage : JanusMessage
    {
        public override string Command { get; set; } = "keepalive";
    }

    private class JanusAckMessage : JanusMessage
    {
        public override string Command { get; set; } = "ack";
    }

    private class JanusCreateMessage : JanusMessage
    {
        public override string Command { get; set; } = "create";

        [JsonPropertyName("data")]
        public JanusCreateMessageData Data { get; set; } = default!;

        public class JanusCreateMessageData
        {
            [JsonPropertyName("id")]
            public ulong Id { get; set; }
        }
    }

    private class JanusAttachMessage : JanusMessage
    {
        public override string Command { get; set; } = "attach";

        [JsonPropertyName("plugin")]
        public string PluginPackageName { get; set; } = default!;
    }

    private class JanusSuccessMessage : JanusMessage
    {
        public override string Command { get; set; } = "success";

        [JsonPropertyName("data")]
        public JanusSuccessMessageData Data { get; set; } = default!;

        public class JanusSuccessMessageData
        {
            [JsonPropertyName("id")]
            public ulong Id { get; set; }
        }
    }

    private class JanusPluginMessage : JanusMessage
    {
        public override string Command { get; set; } = "message";

        [JsonPropertyName("handle_id")]
        public ulong PluginHandleId { get; set; }

        [JsonPropertyName("body")]
        public JanusPluginMessageBody Body { get; set; } = default!;
    }

    [JsonConverter(typeof(JanusPluginMessageBodyConverter))]
    private class JanusPluginMessageBody
    { }

    private class JanusPluginMessageBodyConverter : JsonConverter<JanusPluginMessageBody>
    {
        public override JanusPluginMessageBody Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException();
            }

            using var jsonDocument = JsonDocument.ParseValue(ref reader);
            var jsonObject = jsonDocument.RootElement.GetRawText();
            if (jsonDocument.RootElement.TryGetProperty("request", out var typeProperty))
            {
                return JsonSerializer.Deserialize<JanusVideoRoomPluginRequestMessageBody>(
                    jsonObject, options);
            }
            else
            {
                return JsonSerializer.Deserialize<JanusVideoRoomPluginResponseMessageBody>(
                    jsonObject, options);
            }
        }

        public override void Write(Utf8JsonWriter writer, JanusPluginMessageBody value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "request",
        IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(JanusCreateMessage), "exists")]
    private class JanusVideoRoomPluginRequestMessageBody : JanusPluginMessageBody
    {
        [JsonPropertyName("request")]
        public virtual string VideoRoomRequest { get; set; } = default!;
    }

    private class JanusVideoRoomPluginExistsRequestMessageBody :
        JanusVideoRoomPluginRequestMessageBody
    {
        public override string VideoRoomRequest { get; set; } = "exists";

        [JsonPropertyName("room")]
        public ulong RoomId { get; set; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "videoroom",
        IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(JanusVideoRoomPluginResponseSuccessMessageBody), "success")]
    private class JanusVideoRoomPluginResponseMessageBody : JanusPluginMessageBody
    {
        [JsonPropertyName("videoroom")]
        public virtual string VideoRoomResponse { get; set; } = default!;
    }

    private class JanusVideoRoomPluginResponseSuccessMessageBody :
        JanusVideoRoomPluginResponseMessageBody
    {
        public override string VideoRoomResponse { get; set; } = "success";

        [JsonPropertyName("room")]
        public ulong RoomId { get; set; }

        [JsonPropertyName("exists")]
        public bool Exists { get; set; }
    }
}