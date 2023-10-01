namespace ZonalJanusAgent;

public interface IJanusClient
{
    Task<string> StartStream(ulong channelId, string sdp);
    Task StopStream(ulong channelId);
}