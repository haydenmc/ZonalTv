using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ZonalTv.Services;

public class JanusServerSettings
{
    public Uri WebsocketUri { get; set; } = default!;
}

public class JanusServerBackgroundService : BackgroundService
{
    private static readonly TimeSpan JANUS_KEEPALIVE_MESSAGE_INTERVAL = new(0, 0, 5);

    private readonly ILogger<JanusServerBackgroundService> _logger;

    private readonly IOptions<JanusServerSettings> _options;

    private Channel<Func<Task>> _taskQueue = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions() {
            SingleReader = true
        });

    public JanusServerBackgroundService(ILogger<JanusServerBackgroundService> logger,
        IOptions<JanusServerSettings> options)
    {
        _logger = logger;
        _options = options;
    }

    protected async override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using ClientWebSocket ws = new();
            try
            {
                var sessionId = await ConnectToJanusAsync(ws, _options.Value.WebsocketUri,
                    cancellationToken);
                while (true)
                {
                    // Start processing incoming messages, queued tasks, and sending
                    // keepalive messages
                    var messageLoopCancellationSource = 
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    await Task.WhenAny(
                        ReadIncomingJanusMessagesAsync(ws, messageLoopCancellationSource.Token),
                        SendKeepAliveMessagesAsync(ws, sessionId,
                            messageLoopCancellationSource.Token),
                        ProcessTaskQueueAsync(messageLoopCancellationSource.Token));

                    // We don't expect to get here unless one of the above tasks exits
                    // prematurely. Cancel the rest of them and let the loop continue.
                    await messageLoopCancellationSource.CancelAsync();
                    _logger.LogError("Janus processing loop interrupted unexpectedly");
                }
            }
            catch (OperationCanceledException e)
            {
                _logger.LogInformation("Shutting down Janus connection...");

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
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private async Task<long> ConnectToJanusAsync(ClientWebSocket webSocket, Uri webSocketUri,
        CancellationToken cancellationToken)
    {
        // Open connection
        _logger.LogInformation("Connecting to Janus at {}...", webSocketUri.ToString());
        webSocket.Options.AddSubProtocol("janus-protocol");
        await webSocket.ConnectAsync(webSocketUri, cancellationToken);

        // Get a session id
        _logger.LogInformation("Connected to Janus, creating session...");
        var infoRequest = new JanusMessage()
        {
            Command = "create"
        };
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

    private async Task ReadIncomingJanusMessagesAsync(ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = 
                await webSocket.ReadJsonAndDeserializeAsync<JanusMessage>(cancellationToken);
            if (message != null)
            {
                _logger.LogInformation("Received {} message from Janus", message.GetType().Name);
            }
        }
    }

    private async Task SendKeepAliveMessagesAsync(ClientWebSocket webSocket, long sessionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // Send keepalive message on a regular interval to keep Janus from closing
            // our session.
            await Task.Delay(JANUS_KEEPALIVE_MESSAGE_INTERVAL, cancellationToken);
            var keepAliveMessage = new JanusMessage()
            {
                Command = "keepalive",
                SessionId = sessionId,
            };
            _logger.LogInformation("Sending Janus keepalive for session ID '{}'...",
                sessionId);
            await webSocket.SendJsonAsync(keepAliveMessage, cancellationToken);
        }
    }

    private async Task ProcessTaskQueueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var task = await _taskQueue.Reader.ReadAsync(cancellationToken);
            await task.Invoke();
        }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "janus",
        IgnoreUnrecognizedTypeDiscriminators = true)]
    [JsonDerivedType(typeof(JanusCreateMessage), "create")]
    private class JanusMessage
    {
        [JsonPropertyName("janus")]
        public string Command { get; set; } = default!;

        [JsonPropertyName("transaction")]
        public string TransactionId { get; set; } = Path.GetRandomFileName();

        [JsonPropertyName("session_id")]
        public long? SessionId { get; set; } = null;
    }

    private class JanusCreateMessage : JanusMessage
    {
        [JsonPropertyName("data")]
        public JanusCreateMessageData Data { get; set; } = default!;

        public class JanusCreateMessageData
        {
            [JsonPropertyName("id")]
            public long Id { get; set; }
        }
    }
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