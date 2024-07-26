using ZonalJanusAgent;
using ZonalJanusAgent.Services;

public partial class JanusStreamManagerService(ILogger<JanusStreamManagerService> logger,
    JanusWebsocketClientService janusClient) : IJanusClient
{
    private class StreamInfo(ulong sessionId)
    {
        public ulong SessionId = sessionId;
        public ulong? VideoRoomHandle;
    }

    private ILogger<JanusStreamManagerService> _logger = logger;
    private JanusWebsocketClientService _janusClient = janusClient;
    private Dictionary<ulong, StreamInfo> _channelStreams = [];

#region IJanusClient
    public async Task<string> StartStreamAsync(ulong channelId, string sdp)
    {
        _logger.LogInformation("Start stream requested for channel '{}' with sdp '{}'", channelId,
            sdp);

        // Find or create Janus session
        // TODO: We should probably kick the existing session out instead of recycling it.
        if (!_channelStreams.TryGetValue(channelId, out StreamInfo? streamInfo))
        {
            var sessionId = await _janusClient.CreateJanusSessionAsync();
            streamInfo = new StreamInfo(sessionId);
            _channelStreams[channelId] = streamInfo;
            _logger.LogInformation("Created session '{}' for channel '{}'", sessionId, channelId);
        }

        // Attach session to videoroom plugin
        streamInfo.VideoRoomHandle = await _janusClient.AttachToJanusVideoRoomPluginAsync(
            streamInfo.SessionId);
        _logger.LogInformation("Attached session '{}' to videoroom plugin with handle '{}'",
            streamInfo.SessionId, streamInfo.VideoRoomHandle);

        // Find or create room
        var roomExists = await _janusClient.DoesJanusVideoRoomExistAsync(streamInfo.SessionId,
            (ulong)streamInfo.VideoRoomHandle, channelId);
        if (!roomExists)
        {
            _logger.LogInformation("Room '{}' does not exist, creating...", channelId);
            await _janusClient.CreateJanusVideoRoomAsync(streamInfo.SessionId,
                (ulong)streamInfo.VideoRoomHandle, channelId);
        }

        // Join and publish
        var sdpAnswer = await _janusClient.JoinAndConfigureVideoRoomAsync(streamInfo.SessionId,
            (ulong)streamInfo.VideoRoomHandle, channelId, sdp);
        _logger.LogInformation("SDP answer for channel '{}': '{}'", channelId, sdpAnswer);

        // TODO: Kick off existing publishers

        return sdpAnswer;
    }

    public async Task StopStreamAsync(ulong channelId)
    {
        _logger.LogInformation("Stop stream requested for channel '{}'", channelId);

        if (!_channelStreams.TryGetValue(channelId, out StreamInfo? streamInfo))
        {
            _logger.LogError("Could not find Janus session for stop stream request on channel '{}'",
                channelId);
            throw new ArgumentException("No existing session for provided channel id.");
        }

        if (streamInfo.VideoRoomHandle == null)
        {
            _logger.LogError("Stream for channel '{}' does not have an associated videoroom handle",
                channelId);
            _channelStreams.Remove(channelId);
            throw new ArgumentException(
                "Channel stream in invalid state (missing videoroom handle)");
        }

        await _janusClient.UnpublishVideoRoomAsync(streamInfo.SessionId,
            (ulong)streamInfo.VideoRoomHandle);
        await _janusClient.LeaveVideoRoomAsync(streamInfo.SessionId,
            (ulong)streamInfo.VideoRoomHandle);
        await _janusClient.DetachFromJanusVideoRoomPluginAsync(streamInfo.SessionId,
            (ulong)streamInfo.VideoRoomHandle);
        await _janusClient.DestroyJanusSessionAsync(streamInfo.SessionId);
        _logger.LogInformation("Stopped stream and destroyed session '{}' for channel '{}'",
            streamInfo.SessionId, channelId);
        _channelStreams.Remove(channelId);
    }
#endregion IJanusClient
}