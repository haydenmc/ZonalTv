using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZonalJanusAgent.Utility;

internal static class WebsocketClientExtensions
{
    public static Task SendStringAsync(this ClientWebSocket ws, string content,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return ws.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length),
            WebSocketMessageType.Text, true, cancellationToken);
    }

    public static Task SendJsonAsync(this ClientWebSocket ws, object content,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content);
        return ws.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length),
            WebSocketMessageType.Text, true, cancellationToken);
    }

    public async static Task<JsonNode?> ReadJsonAndDeserializeAsync(this ClientWebSocket ws,
        CancellationToken cancellationToken)
    {
        ArraySegment<byte> buffer = new(new byte[8192]);
        var result = await ws.ReceiveAsync(buffer, cancellationToken);
        if ((result.MessageType != WebSocketMessageType.Text) ||
            !result.EndOfMessage || (buffer.Array == null))
        {
            throw new ApplicationException("Received unexpected websocket message type from Janus");
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
    }

    public async static Task<T?> ReadJsonAndDeserializeAsync<T>(this ClientWebSocket ws,
        CancellationToken cancellationToken)
    {
        ArraySegment<byte> buffer = new(new byte[8192]);
        var result = await ws.ReceiveAsync(buffer, cancellationToken);
        if ((result.MessageType != WebSocketMessageType.Text) ||
            !result.EndOfMessage || (buffer.Array == null))
        {
            throw new ApplicationException("Received unexpected websocket message type from Janus");
        }
        var messageString = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
        return JsonSerializer.Deserialize<T>(messageString);
    }
}