using Microsoft.Extensions.Options;
using System.Net.WebSockets;
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

    private readonly BufferBlock<(JanusMessage message,
        TaskCompletionSource<JanusMessage> responseCompletionSource)> _outgoingMessageQueue = new();

    private readonly Dictionary<string, TaskCompletionSource<JanusMessage>>
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
                            var message = await ws.ReadJsonAndDeserializeAsync<JanusMessage>(
                                cancellationToken);
                            if (message != null)
                            {
                                // handle the message
                                HandleIncomingJanusMessage(message);
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
                            _logger.LogDebug("Sending message with transaction id '{}' to Janus...",
                                message.TransactionId);
                            if (_outstandingRequests.ContainsKey(message.TransactionId))
                            {
                                _logger.LogError(
                                    "Duplicate transaction id '{}' when sending Janus message",
                                    message.TransactionId);
                                throw new ArgumentException("This transaction ID already exists.");
                            }
                            _outstandingRequests.Add(message.TransactionId,
                                responseCompletionSource);
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
                                var keepAliveMessage = new JanusKeepAliveMessage()
                                    {
                                        SessionId = session.SessionId,
                                    };
                                await ws.SendJsonAsync(keepAliveMessage, cancellationToken);
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

    private void HandleIncomingJanusMessage(JanusMessage message)
    {
        if (_outstandingRequests.TryGetValue(message.TransactionId,
            out TaskCompletionSource<JanusMessage>? value))
        {
            _logger.LogDebug("Received response message to transaction '{}'",
                message.TransactionId);
            value?.SetResult(message);
        }
    }

    private Task<JanusMessage> SendJanusRequestAsync(JanusMessage message)
    {
        _logger.LogDebug("Posting message with transaction id '{}' for sending to Janus...",
            message.TransactionId);
        var completionSource = new TaskCompletionSource<JanusMessage>();
        _outgoingMessageQueue.Post((message, completionSource));
        return completionSource.Task;
    }

    private async Task<JanusSession> CreateJanusSessionAsync()
    {
        var infoRequest = new JanusCreateMessage();
        var response = await SendJanusRequestAsync(infoRequest);
        if ((response == null) ||
            (response.TransactionId != response.TransactionId) ||
            !(response is JanusSuccessMessage))
        {
            _logger.LogError("Invalid response when requesting Janus session ID.");
            throw new ApplicationException("Invalid response when requesting Janus session ID.");
        }
        var sessionId = (response as JanusSuccessMessage).Data.Id;
        var result = new JanusSession(sessionId);
        _logger.LogInformation("Created new Janus session, ID '{}'.", sessionId);
        return result;
    }

    private async Task AttachToJanusVideoRoomPluginAsync(JanusSession session)
    {
        var attachRequest = new JanusAttachMessage()
        {
            PluginPackageName = "janus.plugin.videoroom",
            SessionId = session.SessionId,
        };
        var attachResponse = await SendJanusRequestAsync(attachRequest);
        if ((attachResponse == null) ||
            (attachResponse.TransactionId != attachRequest.TransactionId) ||
            !(attachResponse is JanusSuccessMessage))
        {
            _logger.LogError("Invalid response when attaching to Janus videoroom plugin.");
            throw new ApplicationException(
                "Invalid response when attaching to Janus videoroom plugin.");
        }
        session.VideoRoomHandle = (attachResponse as JanusSuccessMessage).Data.Id;
    }

    private async Task<bool> DoesJanusVideoRoomExistAsync(JanusSession session, ulong roomId)
    {
        var request = new JanusPluginMessage()
            {
                SessionId = session.SessionId,
                PluginHandleId = session.VideoRoomHandle,
                Body = new JanusVideoRoomPluginExistsRequestMessageBody()
                    {
                        RoomId = roomId
                    },
            };
        var response = await SendJanusRequestAsync(request);
        if ((response == null) ||
            (response.TransactionId != request.TransactionId) ||
            !(response is JanusPluginMessage) ||
            !((response as JanusPluginMessage).Body is
                JanusVideoRoomPluginResponseSuccessMessageBody))
        {
            _logger.LogError("Invalid response when attaching to Janus videoroom plugin.");
            throw new ApplicationException(
                "Invalid response when attaching to Janus videoroom plugin.");
        }

        return ((response as JanusPluginMessage).Body as
            JanusVideoRoomPluginResponseSuccessMessageBody).Exists;
    }

#region IJanusClient
    public async Task<string> StartStreamAsync(ulong channelId, string sdp)
    {
        _logger.LogInformation("Start stream requested for channel '{}'", channelId);

        // 1. Find or create Janus session
        if (!_sessions.TryGetValue(channelId, out JanusSession session))
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
            // Create room
        }

        // 4. Join and configure room
        // 5. Publish
        throw new NotImplementedException();
    }

    public Task StopStreamAsync(ulong channelId)
    {
        throw new NotImplementedException();
    }
#endregion IJanusClient
}