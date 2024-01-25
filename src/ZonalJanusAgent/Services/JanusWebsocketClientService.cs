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
    IOptions<JanusWebsocketClientServiceSettings> options) : BackgroundService, IJanusClient
{
    private static readonly TimeSpan JANUS_KEEPALIVE_MESSAGE_INTERVAL = new(0, 0, 5);
    private readonly ILogger<JanusWebsocketClientService> _logger = logger;
    private readonly IOptions<JanusWebsocketClientServiceSettings> _options = options;

    private readonly Dictionary<ulong, JanusSession> _sessions = [];

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
                                    "Message queued with no 'transaction' property. Skipping.");
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
                            foreach (var session in _sessions.Values)
                            {
                                var keepAliveMessage = JanusMessages.MakeJanusRequestMessage(
                                    "keepalive", session.SessionId);
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
            _logger.LogError("Received message with no 'transaction' property.");
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

    private async Task<JanusSession> CreateJanusSessionAsync()
    {
        var createRequest = JanusMessages.MakeJanusRequestMessage("create");
        var response = await SendJanusRequestAsync(createRequest);
        var sessionId = response["data"]?["id"]?.GetValue<ulong>();
        if (sessionId == null)
        {
            throw new ApplicationException("Received create response from Janus that did not " + 
                $"contain a session id: {response.ToJsonString()}");
        }
        var result = new JanusSession((ulong)sessionId);
        _logger.LogInformation("Created new Janus session, ID '{}'.", sessionId);
        return result;
    }

    private async Task AttachToJanusVideoRoomPluginAsync(JanusSession session)
    {
        var attachRequest = JanusMessages.MakeJanusAttachRequestMessage(session.SessionId,
            "janus.plugin.videoroom");
        var attachResponse = await SendJanusRequestAsync(attachRequest);
        var handleId = attachResponse["data"]?["id"]?.GetValue<ulong>();
        if (handleId == null)
        {
            throw new ApplicationException("Received attach response from Janus that did not " + 
                $"contain a session id: {attachResponse.ToJsonString()}");
        }
        session.VideoRoomHandle = (ulong)handleId;
    }

    private async Task<bool> DoesJanusVideoRoomExistAsync(JanusSession session, ulong roomId)
    {
        var request = JanusMessages.MakeJanusVideoRoomExistsMessage(session.SessionId,
            session.VideoRoomHandle, roomId);
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

    private async Task<ulong> CreateJanusVideoRoomAsync(JanusSession session, ulong roomId)
    {
        var request = JanusMessages.MakeJanusVideoRoomCreateMessage(session.SessionId,
            session.VideoRoomHandle, roomId);
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

    private async Task<string> JoinAndConfigureVideoRoomAsync(JanusSession session, ulong roomId,
        string sdp)
    {
        var request = JanusMessages.MakeJanusVideoRoomJoinAndConfigureMessage(session.SessionId,
            session.VideoRoomHandle, roomId, sdp);
        var response = await SendJanusRequestAsync(request);
        if ((response["jsep"] == null) || (response["plugindata"] == null) ||
            (response["plugindata"]!["data"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"] == null) ||
            (response["plugindata"]!["data"]!["videoroom"]!.GetValue<string>() != "joined"))
        {
            throw new ApplicationException("Invalid response when creating Janus room: " +
                $"{response.ToJsonString()}");
        }

        return response["jsep"]!["sdp"]!.GetValue<string>();
    }

#region IJanusClient
    public async Task<string> StartStreamAsync(ulong channelId, string sdp)
    {
        _logger.LogInformation("Start stream requested for channel '{}' with sdp '{}'", channelId,
            sdp);

        // 1. Find or create Janus session
        if (!_sessions.TryGetValue(channelId, out JanusSession? session))
        {
            session = await CreateJanusSessionAsync();
            _sessions[channelId] = session;
            _logger.LogInformation("Created session '{}' for channel '{}'", session.SessionId,
                channelId);
        }

        // 2. Attach session to videoroom plugin
        await AttachToJanusVideoRoomPluginAsync(session);
        _logger.LogInformation("Attached session '{}' to videoroom plugin with handle '{}'",
            session.SessionId, session.VideoRoomHandle);

        // 3. Find or create room
        var roomExists = await DoesJanusVideoRoomExistAsync(session, channelId);
        if (!roomExists)
        {
            _logger.LogInformation("Room '{}' does not exist, creating...", channelId);
            await CreateJanusVideoRoomAsync(session, channelId);
        }

        // 4. Join and publish
        var sdpAnswer = await JoinAndConfigureVideoRoomAsync(session, channelId, sdp);
        _logger.LogInformation("SDP answer for channel '{}': '{}'", channelId, sdpAnswer);

        return sdpAnswer;
    }

    public Task StopStreamAsync(ulong channelId)
    {
        throw new NotImplementedException();
    }
#endregion IJanusClient
}