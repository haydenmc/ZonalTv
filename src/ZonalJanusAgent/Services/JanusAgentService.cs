using Grpc.Core;

namespace ZonalJanusAgent.Services;

public class JanusAgentService : JanusAgent.JanusAgentBase
{
    private readonly ILogger<JanusAgentService> _logger;
    
    private IJanusClient _janusClient;

    public JanusAgentService(ILogger<JanusAgentService> logger, IJanusClient janusClient)
    {
        _logger = logger;
        _janusClient = janusClient;
    }

    public override async Task<StartStreamResponse> StartStream(StartStreamRequest request,
        ServerCallContext context)
    {
        var sdp = await _janusClient.StartStream(request.ChannelId, request.Sdp);
        return new StartStreamResponse() {
            Sdp = sdp
        };
    }
}