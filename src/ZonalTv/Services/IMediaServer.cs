namespace ZonalTv.Services;

public interface IMediaServer
{
    Task<string> StartStreamAsync(ulong channelId, string sdp);

    Task StopStreamAsync(ulong channelId);
}