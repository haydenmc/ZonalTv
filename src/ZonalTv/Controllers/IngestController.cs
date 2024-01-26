using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using ZonalTv.Services;
using ZonalTv.Utility;

namespace ZonalTv.Controllers;

public class IngestController(ILogger<IngestController> logger, IMediaServer mediaServer) :
    Controller
{
    private readonly ILogger<IngestController> _logger = logger;
    private readonly IMediaServer _mediaServer = mediaServer;

    [HttpPost]
    [Route("/ingest")]
    public async Task<IActionResult> StartStream()
    {
        ulong channelId = 1;
        var sdp = await new StreamReader(Request.Body).ReadToEndAsync();
        // TODO authenticate stream, verify content type is application/sdp, verify body is valid SDP...
        if (sdp == null)
        {
            return BadRequest("Invalid SDP in POST body");
        }

        try
        {
            var sdpAnswer = await _mediaServer.StartStreamAsync(channelId, sdp);
            return new WhipActionResult(sdpAnswer, $"/ingest/{channelId}");
        }
        catch (Exception e)
        {
            _logger.LogError("Janus Agent StartStream error: '{}'", e.Message);
            return StatusCode(500, "Could not start stream");
        }
    }

    [HttpDelete]
    [Route("/ingest/{id}")]
    public async Task<IActionResult> StopStream(ulong id)
    {
        await _mediaServer.StopStreamAsync(id);
        return Ok();
    }
}