using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Design.Internal;

namespace ZonalTv.Utility;

public class WhipActionResult : IActionResult
{
    private string _sdpAnswer;
    private string _location;

    public WhipActionResult(string sdpAnswer, string location)
    {
        _sdpAnswer = sdpAnswer;
        _location = location;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        context.HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;
        context.HttpContext.Response.Headers["Content-Type"] = "application/sdp";
        context.HttpContext.Response.Headers["Location"] = _location;
        await context.HttpContext.Response.BodyWriter.WriteAsync(
            Encoding.UTF8.GetBytes(_sdpAnswer));
    }
}