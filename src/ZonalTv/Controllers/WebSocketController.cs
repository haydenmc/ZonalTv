using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Mvc;
using ZonalTv.Data;
using ZonalTv.Services;

namespace ZonalTv.Controllers;

public class WebSocketController(ILogger<IngestController> logger, IMediaServer mediaServer) :
    ControllerBase
{
    private const string WebSocketSubProtocol = "zonal-ws-api";
    private readonly ILogger<IngestController> _logger = logger;
    private readonly IMediaServer _mediaServer = mediaServer;

    public class WebSocketConnection(ILogger<WebSocketConnection> logger, IMediaServer mediaServer,
        WebSocket webSocket)
    {
        private ILogger<WebSocketConnection> _logger = logger;
        private IMediaServer _mediaServer = mediaServer;
        private WebSocket _webSocket = webSocket;

        public async Task HandleConnectionAsync(CancellationToken ct)
        {
            // Set up a simple message processing loop
            BufferBlock<Func<Task>> taskQueue = new();
            var socketCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            
            Task[] tasks = {
                // Task to read incoming websocket messages
                Task.Run(async () => {
                    var buffer = new byte[1024 * 4];
                    while (true)
                    {
                        var receiveResult = await _webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), ct);
                        if (receiveResult.CloseStatus.HasValue)
                        {
                            socketCancellationTokenSource.Cancel();
                            break;
                        }
                        var receiveString = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                        await taskQueue.SendAsync(async () =>
                            await HandleIncomingMessageAsync(receiveString));
                    }
                })
            };

            try
            {
                while (!socketCancellationTokenSource.Token.IsCancellationRequested)
                {
                    // process message queue
                    var task = await taskQueue.ReceiveAsync(socketCancellationTokenSource.Token);
                    await task();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Shutting down WebSocketController...");
            }

            await Task.WhenAll(tasks);
        }

        private Task HandleIncomingMessageAsync(string messageString)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebSocketMessage>(messageString);
                if (message == null)
                {
                    throw new ArgumentException("Null JSON object provided");
                }
                switch (message.Type)
                {
                case WebSocketMessageKind.Watch:
                    if (message.ChannelId == null)
                    {
                        throw new ArgumentException("Invalid channel ID provided");
                    }
                    
                    break;
                }
            }
            catch (Exception e)
            {
                _logger.LogError("Couldn't handle incoming WebSocket message: {}", e.Message);
            }
            return Task.CompletedTask;
        }
    }

    [Route("/ws")]
    public async Task Get(CancellationToken ct)
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(
                WebSocketSubProtocol);
            var connectionLogger = ActivatorUtilities
                .GetServiceOrCreateInstance<ILogger<WebSocketConnection>>(
                    HttpContext.RequestServices);
            var connectionMediaServer = ActivatorUtilities.GetServiceOrCreateInstance<IMediaServer>(
                HttpContext.RequestServices);
            await new WebSocketConnection(connectionLogger, mediaServer, webSocket)
                .HandleConnectionAsync(ct);
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
}