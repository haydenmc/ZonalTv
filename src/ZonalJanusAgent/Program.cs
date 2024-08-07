using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ZonalJanusAgent;
using ZonalJanusAgent.Services;
using ZonalJanusAgent.Utility;

var socketPath = Path.Combine(Path.GetTempPath(), "zonal-janus-agent.socket");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenUnixSocket(socketPath, listenOptions => 
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

// Add JanusClient as a hosted service, and also as an implementation of IJanusClient
builder.Services.AddSingleton<JanusWebsocketClientService>();
builder.Services.AddSingleton<JanusStreamManagerService>();
builder.Services.AddSingleton<IJanusClient>(sp =>
    sp.GetRequiredService<JanusStreamManagerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<JanusWebsocketClientService>());
builder.Services.Configure<JanusWebsocketClientServiceSettings>(options =>
{
    options.WebsocketUri = new Uri("ws://127.0.0.1:8188");
});

// Add JanusAgent gRPC service
builder.Services.AddGrpc(options => options.Interceptors.Add<ExceptionInterceptor>());

var host = builder.Build();
host.MapGrpcService<JanusAgentService>();
if (File.Exists(socketPath))
{
    File.Delete(socketPath);
}
host.Run();
