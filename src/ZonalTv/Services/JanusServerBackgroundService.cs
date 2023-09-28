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
            try
            {
                // Open connection
                _logger.LogInformation("Connecting to Janus at {}...",
                    _options.Value.WebsocketUri.ToString());
                using ClientWebSocket ws = new();
                ws.Options.AddSubProtocol("janus-protocol");
                await ws.ConnectAsync(_options.Value.WebsocketUri, cancellationToken);

                // Get a session id
                _logger.LogInformation("Connected to Janus");
                var infoRequest = new JanusMessage()
                {
                    Command = "create"
                };
                await ws.SendJsonAsync(infoRequest, cancellationToken);
                var infoResponse = await ws.ReadJsonAndDeserializeAsync<JanusCreateResponse>(
                    cancellationToken);
                if ((infoResponse == null) ||
                    (infoResponse.TransactionId != infoRequest.TransactionId))
                {
                    _logger.LogError("Invalid response when requesting Janus session ID.");
                    continue;
                }
                var sessionId = infoResponse.Data.Id;
                _logger.LogInformation("Received Janus session ID '{}'.", sessionId);

                while (true)
                {
                    // Wait for incoming commands, or fire a keep alive every 5 seconds.
                    var readCancellationSource = 
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var readTask = _taskQueue.Reader.ReadAsync(
                        readCancellationSource.Token).AsTask();
                    if (await Task.WhenAny(readTask, Task.Delay(5000, CancellationToken.None))
                        == readTask)
                    {
                        await (await readTask).Invoke();
                    }
                    else
                    {
                        readCancellationSource.Cancel();
                        // Send keepalive message
                        var keepAliveMessage = new JanusSessionMessage()
                        {
                            Command = "keepalive",
                            SessionId = sessionId,
                        };
                        _logger.LogInformation("Sending Janus keepalive for session ID '{}'...",
                            sessionId);
                        await ws.SendJsonAsync(keepAliveMessage, cancellationToken);
                    }
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

    private class JanusMessage
    {
        [JsonPropertyName("janus")]
        public string Command { get; set; } = default!;

        [JsonPropertyName("transaction")]
        public string TransactionId { get; set; } = Path.GetRandomFileName();
    }

    private class JanusSessionMessage : JanusMessage
    {
        [JsonPropertyName("session_id")]
        public long SessionId { get; set; } = default!;
    }

    private class JanusCreateResponse : JanusMessage
    {
        [JsonPropertyName("data")]
        public JanusCreateResponseData Data { get; set; } = default!;

        public class JanusCreateResponseData
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