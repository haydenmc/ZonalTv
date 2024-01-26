using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ZonalJanusAgent.Services;

public partial class JanusWebsocketClientService
{
    private abstract class JanusMessages
    {
        public static JsonObject MakeJanusRequestMessage(string command, ulong? sessionId = null)
        {
            var contents = new List<KeyValuePair<string, JsonNode?>> {
                new ("janus", command),
                new ("transaction", Path.GetRandomFileName()),
            };
            if (sessionId != null)
            {
                contents.Add(new("session_id", sessionId));
            }
            return new JsonObject(contents);
        }

        public static JsonObject MakeJanusAttachRequestMessage(ulong sessionId,
            string pluginPackageName)
        {
            var message = MakeJanusRequestMessage("attach", sessionId);
            message["plugin"] = pluginPackageName;
            return message;
        }

        public static JsonObject MakeJanusDetachRequestMessage(ulong sessionId, ulong handleId)
        {
            var message = MakeJanusRequestMessage("detach", sessionId);
            message["handle_id"] = handleId;
            return message;
        }

        public static JsonObject MakeJanusPluginMessage(ulong sessionId, ulong handleId)
        {
            var message = MakeJanusRequestMessage("message", sessionId);
            message["handle_id"] = handleId;
            message["body"] = new JsonObject();
            return message;
        }

        public static JsonObject MakeJanusVideoRoomExistsMessage(ulong sessionId,
            ulong handleId, ulong roomId)
        {
            var message = MakeJanusPluginMessage(sessionId, handleId);
            message["body"]!["request"] = "exists";
            message["body"]!["room"] = roomId;
            return message;
        }

        public static JsonObject MakeJanusVideoRoomCreateMessage(ulong sessionId, ulong handleId,
            ulong roomId)
        {
            var message = MakeJanusPluginMessage(sessionId, handleId);
            message["body"]!["request"] = "create";
            message["body"]!["room"] = roomId;
            // TODO: Make these configurable
            message["body"]!["audiocodec"] = "opus";
            message["body"]!["videocodec"] = "h264";
            return message;
        }

        public static JsonObject MakeJanusVideoRoomJoinAndConfigureMessage(ulong sessionId,
            ulong handleId, ulong roomId, string sdp)
        {
            var message = MakeJanusPluginMessage(sessionId, handleId);
            message["body"]!["request"] = "joinandconfigure";
            message["body"]!["ptype"] = "publisher";
            message["body"]!["room"] = roomId;
            message["jsep"] = new JsonObject(new List<KeyValuePair<string, JsonNode?>>{
                new ("type", "offer"),
                new ("sdp", sdp),
            });
            return message;
        }

        public static JsonObject MakeJanusVideoRoomUnpublishMessage(ulong sessionId, ulong handleId)
        {
            var message = MakeJanusPluginMessage(sessionId, handleId);
            message["body"]!["request"] = "unpublish";
            return message;
        }

        public static JsonObject MakeJanusVideoRoomLeaveMessage(ulong sessionId, ulong handleId)
        {
            var message = MakeJanusPluginMessage(sessionId, handleId);
            message["body"]!["request"] = "leave";
            return message;
        }
    }

    private class JanusSession(ulong sessionId)
    {
        public readonly ulong SessionId = sessionId;
        public ulong VideoRoomHandle;
    }
}