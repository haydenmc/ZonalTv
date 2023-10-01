using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using ZonalJanusAgent;
using ZonalJanusAgent.Services;

var socketPath = Path.Combine(Path.GetTempPath(), "zonal-janus-agent.socket");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenUnixSocket(socketPath);
});

// Add JanusClient as a hosted service, and also as an implementation of IJanusClient
builder.Services.AddSingleton<JanusWebsocketClientService>();
builder.Services.AddSingleton<IJanusClient>(sp =>
    sp.GetRequiredService<JanusWebsocketClientService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<JanusWebsocketClientService>());
builder.Services.Configure<JanusWebsocketClientServiceSettings>(options =>
{
    options.WebsocketUri = new Uri("ws://127.0.0.1:8188");
});

// Add JanusAgent gRPC service
builder.Services.AddGrpc();

var host = builder.Build();

host.MapGrpcService<JanusAgentService>();

host.Run();
