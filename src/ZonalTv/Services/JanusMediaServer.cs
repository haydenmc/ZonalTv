using Grpc.Net.Client;
using System.Net;
using System.Net.Sockets;
using ZonalJanusAgent;
using ZonalTv.Utility;

namespace ZonalTv.Services;

public class JanusMediaServer : IMediaServer
{
    private readonly ILogger<JanusMediaServer> _logger;
    private readonly JanusAgent.JanusAgentClient _janusAgent;

    public JanusMediaServer(ILogger<JanusMediaServer> logger)
    {
        _logger = logger;

        // Connect to Janus Agent via Unix Domain Sockets
        var udsEndpoint = new UnixDomainSocketEndPoint(
            Path.Combine(Path.GetTempPath(), "zonal-janus-agent.socket"));
        var grpcConnectionFactory = new UnixDomainSocketsConnectionFactory(udsEndpoint);
        var socketsHttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = grpcConnectionFactory.ConnectAsync
            };
        var grpcChannel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = socketsHttpHandler
            });
        _janusAgent = new JanusAgent.JanusAgentClient(grpcChannel);
    }

#region IMediaServer
    public async Task<string> StartStreamAsync(ulong channelId, string sdp)
    {
        var response = await _janusAgent.StartStreamAsync(new StartStreamRequest
            {
                ChannelId = channelId,
                Sdp = sdp,
            });
        return response.Sdp;
    }

    public async Task StopStreamAsync(ulong channelId)
    {
        await _janusAgent.StopStreamAsync(new StopStreamRequest{ ChannelId = channelId });
    }
#endregion IMediaServer
}