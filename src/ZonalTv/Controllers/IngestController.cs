using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using ZonalTv.Services;

namespace ZonalTv.Controllers;

public class IngestController : Controller
{
    private readonly ILogger<IngestController> _logger;
    private readonly IMediaServer _mediaServer;

    public IngestController(ILogger<IngestController> logger, IMediaServer mediaServer)
    {
        _logger = logger;
        _mediaServer = mediaServer;
    }

    [HttpPost]
    [Route("/ingest")]
    public async Task<IActionResult> StartStream()
    {
        var sdp = await new StreamReader(Request.Body).ReadToEndAsync();
        // TODO authenticate stream, verify content type is application/sdp, verify body is valid SDP...
        if (sdp == null)
        {
            return BadRequest("Invalid SDP in POST body");
        }

        try
        {
            var startResult = await _mediaServer.StartStreamAsync(1ul, sdp);
            var result = Content(startResult, "application/sdp");
            result.StatusCode = (int)HttpStatusCode.Created;
            return result;
        }
        catch (Exception e)
        {
            _logger.LogError("Janus Agent StartStream error: '{}'", e.Message);
            return StatusCode(500, "Could not start stream");
        }
    }
}