using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ZonalJanusAgent.Services;

public class JanusWebsocketClientServiceSettings
{
    public Uri WebsocketUri { get; set; } = default!;
}

public class JanusWebsocketClientService : BackgroundService, IJanusClient
{
    private static readonly TimeSpan JANUS_KEEPALIVE_MESSAGE_INTERVAL = new(0, 0, 5);
    private readonly ILogger<JanusWebsocketClientService> _logger;
    private readonly IOptions<JanusWebsocketClientServiceSettings> _options;


    public JanusWebsocketClientService(ILogger<JanusWebsocketClientService> logger,
        IOptions<JanusWebsocketClientServiceSettings> options)
    {
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Beginning execution");
        while (!cancellationToken.IsCancellationRequested)
        {
            using ClientWebSocket ws = new();
            try
            {
                var mainSessionId = await ConnectToJanusAsync(ws, _options.Value.WebsocketUri,
                    cancellationToken);

                // Start processing incoming messages and sending keepalive messages
                var incomingMessageTask = ws.ReadJsonAndDeserializeAsync<JanusMessage>(
                    cancellationToken);
                var keepAliveTask = Task.Delay(JANUS_KEEPALIVE_MESSAGE_INTERVAL, cancellationToken);
                while (true)
                {
                    var completedTask = await Task.WhenAny(incomingMessageTask, keepAliveTask);
                    if (completedTask == incomingMessageTask)
                    {
                        var message = await incomingMessageTask;
                        if (message != null)
                        {
                            _logger.LogInformation("Received '{}' message", message.GetType().Name);
                            incomingMessageTask = ws.ReadJsonAndDeserializeAsync<JanusMessage>(
                                cancellationToken);
                        }
                    }
                    else if (completedTask == keepAliveTask)
                    {
                        var keepAliveMessage = new JanusKeepAliveMessage()
                        {
                            SessionId = mainSessionId,
                        };
                        _logger.LogInformation("Sending Janus keepalive for session ID '{}'...",
                            mainSessionId);
                        await ws.SendJsonAsync(keepAliveMessage, cancellationToken);
                        keepAliveTask = Task.Delay(JANUS_KEEPALIVE_MESSAGE_INTERVAL,
                            cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Shutting down...");

                // Try to gracefully close the websocket if it's still open
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        CancellationTokenSource exitCancellationTokenSource = new(5000);
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Exit requested",
                            exitCancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError("Timed out attempting to close Janus websocket connection.");
                }
            }
            catch (Exception e)
            {
                _logger.LogError("Exception while communicating with Janus websocket server: '{}'",
                    e.Message);
                try
                {
                    await Task.Delay(5000, cancellationToken);
                }
                catch (OperationCanceledException) {}
            }
        }
        _logger.LogInformation("Shutdown complete");
    }

    private async Task<ulong> ConnectToJanusAsync(ClientWebSocket webSocket, Uri webSocketUri,
        CancellationToken cancellationToken)
    {
        // Open connection
        _logger.LogInformation("Connecting to Janus at {}...", webSocketUri.ToString());
        webSocket.Options.AddSubProtocol("janus-protocol");
        await webSocket.ConnectAsync(webSocketUri, cancellationToken);

        // Get a session id
        _logger.LogInformation("Connected to Janus, creating main session...");
        var infoRequest = new JanusCreateMessage();
        await webSocket.SendJsonAsync(infoRequest, cancellationToken);
        var infoResponse = await webSocket.ReadJsonAndDeserializeAsync<JanusCreateMessage>(
            cancellationToken);
        if ((infoResponse == null) ||
            (infoResponse.TransactionId != infoRequest.TransactionId))
        {
            _logger.LogError("Invalid response when requesting Janus session ID.");
            throw new ApplicationException("Invalid response when requesting Janus session ID.");
        }
        var sessionId = infoResponse.Data.Id;
        _logger.LogInformation("Received Janus session ID '{}'.", sessionId);

        return sessionId;
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "janus",
        IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(JanusCreateMessage), "create")]
    [JsonDerivedType(typeof(JanusKeepAliveMessage), "keepalive")]
    [JsonDerivedType(typeof(JanusAckMessage), "ack")]
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

#region IJanusClient
    public Task<string> StartStream(ulong channelId, string sdp)
    {
        throw new NotImplementedException();
    }

    public Task StopStream(ulong channelId)
    {
        throw new NotImplementedException();
    }
#endregion IJanusClient
}

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