using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;
using ZonalJanusAgent.Utility;

namespace ZonalJanusAgent.Services;

public class JanusWebsocketClientServiceSettings
{
    public Uri WebsocketUri { get; set; } = default!;
}

public partial class JanusWebsocketClientService(ILogger<JanusWebsocketClientService> logger,
    IOptions<JanusWebsocketClientServiceSettings> options) : BackgroundService
{
    private static readonly TimeSpan JANUS_KEEPALIVE_MESSAGE_INTERVAL = new(0, 0, 5);
    private readonly ILogger<JanusWebsocketClientService> _logger = logger;
    private readonly IOptions<JanusWebsocketClientServiceSettings> _options = options;

    private readonly HashSet<ulong> _sessions = [];

    private readonly BufferBlock<(JsonObject message,
        TaskCompletionSource<JsonObject>? responseCompletionSource)> _outgoingMessageQueue = new();

    private readonly Dictionary<string, TaskCompletionSource<JsonObject>>
        _outstandingRequests = [];

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Beginning execution");
        while (!cancellationToken.IsCancellationRequested)
        {
            using ClientWebSocket ws = new();
            try
            {
                // Open connection
                _logger.LogInformation("Connecting to Janus at {}...",
                    _options.Value.WebsocketUri.ToString());
                ws.Options.AddSubProtocol("janus-protocol");
                await ws.ConnectAsync(_options.Value.WebsocketUri, cancellationToken);
                _logger.LogInformation("Connected to Janus.");

                // Start message loop
                _logger.LogInformation("Starting message loop...");
                // Handles incoming messages from Janus
                var incomingMessageTask = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var message = await ws.ReadJsonAndDeserializeAsync(cancellationToken);
                            if (message != null && (message.GetValueKind() == JsonValueKind.Object))
                            {
                                // handle the message
                                HandleIncomingJanusMessage(message.AsObject());
                            }
                        }
                    }, cancellationToken);

                // Handles sending messages to Janus
                var outgoingMessageTask = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var (message, responseCompletionSource) =
                                await _outgoingMessageQueue.ReceiveAsync(cancellationToken);
                            var transactionId = message["transaction"]?.GetValue<string>();
                            if (transactionId == null)
                            {
                                _logger.LogError(
                                    "Message queued with no 'transaction' property: {}",
                                    message.ToJsonString());
                                continue;
                            }
                            _logger.LogDebug("Sending message with transaction id '{}' to Janus...",
                                transactionId);
                            if (_outstandingRequests.ContainsKey(transactionId))
                            {
                                _logger.LogError(
                                    "Duplicate transaction id '{}' when sending Janus message",
                                    transactionId);
                                throw new ArgumentException("This transaction ID already exists.");
                            }
                            _outstandingRequests.Add(transactionId, responseCompletionSource);
                            await ws.SendJsonAsync(message, cancellationToken);
                        }
                    }, cancellationToken);

                // Handles sending a periodic keep-alive message to Janus as required
                // by the Janus websockets API
                var keepAliveTask = Task.Run(async () => {
                        while (true)
                        {
                            await Task.Delay(JANUS_KEEPALIVE_MESSAGE_INTERVAL, cancellationToken);
                            foreach (var sessionId in _sessions)
                            {
                                var keepAliveMessage = JanusMessages.MakeJanusRequestMessage(
                                    "keepalive", sessionId);
                                // Fire and forget
                                SendJanusRequestWithoutResponse(keepAliveMessage);
                            }
                        }
                    }, cancellationToken);

                // These tasks should run continuously until cancellation or exception
                var completedTask = await Task.WhenAny(incomingMessageTask, outgoingMessageTask,
                    keepAliveTask);

                // TODO handle completion
                _logger.LogError("A Janus message loop task ended unexpectedly.");
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

    private void HandleIncomingJanusMessage(JsonObject message)
    {
        if (message["transaction"] == null)
        {
            _logger.LogError("Received message with no 'transaction' property: {}",
                message.ToJsonString());
        }
        else if (_outstandingRequests.TryGetValue(message["transaction"]!.GetValue<string>(),
            out TaskCompletionSource<JsonObject>? value))
        {
            var transactionId = message["transaction"]!.GetValue<string>();
            if (message["janus"]?.GetValue<string>() == "ack")
            {
                _logger.LogInformation("Janus acknowledged request transaction '{}'",
                    transactionId);
            }
            else
            {
                _logger.LogInformation("Received response to transaction '{}'", transactionId);
                _outstandingRequests.Remove(transactionId);
                value?.SetResult(message);
            }
        }
    }

    private void SendJanusRequestWithoutResponse(JsonObject message)
    {
        _logger.LogDebug("Queueing message with transaction id '{}'...",
            message["transaction"]);
        _outgoingMessageQueue.Post((message, null));
    }

    private Task<JsonObject> SendJanusRequestAsync(JsonObject message)
    {
        _logger.LogDebug("Queueing message with transaction id '{}'...",
            message["transaction"]);
        var completionSource = new TaskCompletionSource<JsonObject>();
        _outgoingMessageQueue.Post((message, completionSource));
        return completionSource.Task;
    }

    public async Task<ulong> CreateJanusSessionAsync()
    {
        var createRequest = JanusMessages.MakeJanusRequestMessage("create");
        var response = await SendJanusRequestAsync(createRequest);
        var sessionId = response["data"]?["id"]?.GetValue<ulong>();
        if (sessionId == null)
        {
            throw new ApplicationException("Received create response from Janus that did not " + 
                $"contain a session id: {response.ToJsonString()}");
        }
        _logger.LogInformation("Created new Janus session, ID '{}'.", sessionId);
        _sessions.Add((ulong)sessionId);
        return (ulong)sessionId;
    }

    public async Task DestroyJanusSessionAsync(ulong sessionId)
    {
        var destroyRequest = JanusMessages.MakeJanusRequestMessage("destroy", sessionId);
        var response = await SendJanusRequestAsync(destroyRequest);
        var janusEvent = response["janus"]?.GetValue<string>();
        if ((janusEvent == null) || (janusEvent != "success"))
        {
            throw new ApplicationException("Received unexpected destroy response from Janus: " + 
                $"{response.ToJsonString()}");
        }
        if (!_sessions.Remove(sessionId))
        {
            _logger.LogError("Destroyed session '{}' that was unaccounted for.", sessionId);
        }
    }

    public async Task<ulong> AttachToJanusVideoRoomPluginAsync(ulong sessionId)
    {
        var attachRequest = JanusMessages.MakeJanusAttachRequestMessage(sessionId,
            "janus.plugin.videoroom");
        var attachResponse = await SendJanusRequestAsync(attachRequest);
        var handleId = attachResponse["data"]?["id"]?.GetValue<ulong>();
        if (handleId == null)
        {
            throw new ApplicationException("Received attach response from Janus that did not " + 
                $"contain a session id: {attachResponse.ToJsonString()}");
        }
        return (ulong)handleId;
    }

    public async Task DetachFromJanusVideoRoomPluginAsync(ulong sessionId, ulong videoRoomHandle)
    {
        var attachRequest = JanusMessages.MakeJanusDetachRequestMessage(sessionId,
            videoRoomHandle);
        var attachResponse = await SendJanusRequestAsync(attachRequest);
        var janusEvent = attachResponse["janus"]?.GetValue<string>();
        if ((janusEvent == null) || (janusEvent != "success"))
        {
            throw new ApplicationException("Received unexpected detach response from Janus: " + 
                $"{attachResponse.ToJsonString()}");
        }
    }

    public async Task<bool> DoesJanusVideoRoomExistAsync(ulong sessionId, ulong videoRoomHandle,
        ulong roomId)
    {
        var request = JanusMessages.MakeJanusVideoRoomExistsMessage(sessionId,
            videoRoomHandle, roomId);
        var response = await SendJanusRequestAsync(request);
        if ((response["plugindata"] == null) || (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "success"))
        {
            throw new ApplicationException("Invalid response when querying Janus room existence: " +
                $"{response.ToJsonString()}");
        }

        return response["plugindata"]!["data"]!["exists"]!.GetValue<bool>();
    }

    public async Task<ulong> CreateJanusVideoRoomAsync(ulong sessionId, ulong videoRoomHandle,
        ulong roomId)
    {
        var request = JanusMessages.MakeJanusVideoRoomCreateMessage(sessionId,
            videoRoomHandle, roomId);
        var response = await SendJanusRequestAsync(request);
        if ((response["plugindata"] == null) || (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "created"))
        {
            throw new ApplicationException("Invalid response when creating Janus room: " +
                $"{response.ToJsonString()}");
        }

        return response["plugindata"]!["data"]!["room"]!.GetValue<ulong>();
    }

    public async Task<string> JoinAsSubscriberVideoRoomAsync(ulong sessionId,
        ulong videoRoomHandle, ulong roomId, ulong publisherId)
    {
        var request = JanusMessages.MakeJanusVideoRoomJoinAsSubscriberMessage(sessionId,
            videoRoomHandle, roomId, publisherId);
        var response = await SendJanusRequestAsync(request);
        if ((response["jsep"] == null) || (response["plugindata"] == null) ||
            (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "attached"))
        {
            throw new ApplicationException("Invalid response when subscribing to Janus room: " +
                $"{response.ToJsonString()}");
        }

        return response["jsep"]!["sdp"]!.GetValue<string>();
    }

    public async Task<(string sdp, ulong publisherId)> JoinAndConfigureVideoRoomAsync(
        ulong sessionId, ulong videoRoomHandle, ulong roomId, string sdp)
    {
        var request = JanusMessages.MakeJanusVideoRoomJoinAndConfigureMessage(sessionId,
            videoRoomHandle, roomId, sdp);
        var response = await SendJanusRequestAsync(request);
        if ((response["jsep"] == null) || (response["plugindata"] == null) ||
            (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "joined") ||
            (response["plugindata"]!["data"]!["id"] == null) ||
            (response["plugindata"]!["data"]!["id"]!.GetValueKind() != JsonValueKind.Number))
        {
            throw new ApplicationException("Invalid response when creating Janus room: " +
                $"{response.ToJsonString()}");
        }

        return (sdp: response["jsep"]!["sdp"]!.GetValue<string>(),
            publisherId: response["plugindata"]!["data"]!["id"]!.GetValue<ulong>());
    }

    public async Task UnpublishVideoRoomAsync(ulong sessionId, ulong videoRoomHandle)
    {
        var request = JanusMessages.MakeJanusVideoRoomUnpublishMessage(sessionId,
            videoRoomHandle);
        var response = await SendJanusRequestAsync(request);
        if ((response["plugindata"] == null) || (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "event") ||
            (response["plugindata"]!["data"]!["unpublished"] == null) ||
            (response["plugindata"]!["data"]!["unpublished"]!.GetValue<string>() != "ok"))
        {
            throw new ApplicationException("Invalid response when unpublishing: " +
                $"{response.ToJsonString()}");
        }
    }

    public async Task LeaveVideoRoomAsync(ulong sessionId, ulong videoRoomHandle)
    {
        var request = JanusMessages.MakeJanusVideoRoomLeaveMessage(sessionId,
            videoRoomHandle);
        var response = await SendJanusRequestAsync(request);
        if ((response["plugindata"] == null) || (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "event") ||
            (response["plugindata"]!["data"]!["leaving"] == null) ||
            (response["plugindata"]!["data"]!["leaving"]!.GetValue<string>() != "ok"))
        {
            throw new ApplicationException("Invalid response when leaving Janus room: " +
                $"{response.ToJsonString()}");
        }
    }
}