using Grpc.Core;

namespace ZonalJanusAgent.Services;

public class JanusAgentService(ILogger<JanusAgentService> logger, IJanusClient janusClient) :
    JanusAgent.JanusAgentBase
{
    private readonly ILogger<JanusAgentService> _logger = logger;
    
    private readonly IJanusClient _janusClient = janusClient;

    public override async Task<StartStreamResponse> StartStream(StartStreamRequest request,
        ServerCallContext context)
    {
        var sdp = await _janusClient.StartStreamAsync(request.ChannelId, request.Sdp);
        return new StartStreamResponse() {
            Sdp = sdp
        };
    }

    public override async Task<StopStreamResponse> StopStream(StopStreamRequest request,
        ServerCallContext context)
    {
        await _janusClient.StopStreamAsync(request.ChannelId);
        return new StopStreamResponse();
    }

    public override async Task WatchStream(IAsyncStreamReader<WatchStreamRequest> requestStream,
        IServerStreamWriter<WatchStreamResponse> responseStream, ServerCallContext context)
    {
        await foreach (var message in requestStream.ReadAllAsync())
        {
            switch (message.RequestMessageCase)
            {
            case WatchStreamRequest.RequestMessageOneofCase.Start:
                break;
            }
        }
    }
}