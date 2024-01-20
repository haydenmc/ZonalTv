namespace ZonalJanusAgent;

public interface IJanusClient
{
    Task<string> StartStreamAsync(ulong channelId, string sdp);
    Task StopStreamAsync(ulong channelId);
}