using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Mvc;
using ZonalTv.Services;

namespace ZonalTv.Controllers;

public class WebSocketController(ILogger<IngestController> logger, IMediaServer mediaServer) :
    ControllerBase
{
    private const string WebSocketSubProtocol = "zonal-ws-api";
    private readonly ILogger<IngestController> _logger = logger;
    private readonly IMediaServer _mediaServer = mediaServer;

    [Route("/ws")]
    public async Task Get(CancellationToken ct)
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(
                WebSocketSubProtocol);
            await HandleWebSocket(webSocket, ct);
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    private async Task HandleWebSocket(WebSocket ws, CancellationToken ct)
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
                    var receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (receiveResult.CloseStatus.HasValue)
                    {
                        socketCancellationTokenSource.Cancel();
                        break;
                    }
                    var receiveString = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    await taskQueue.SendAsync(async () =>
                        await HandleIncomingWebSocketMessageAsync(receiveString));
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
        catch ()

        await Task.WhenAll(tasks);
    }

    private Task HandleIncomingWebSocketMessageAsync(string message)
    {
        return Task.CompletedTask;
    }
}