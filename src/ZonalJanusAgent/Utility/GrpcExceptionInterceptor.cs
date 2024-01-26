using Grpc.Core;
using Grpc.Core.Interceptors;

namespace ZonalJanusAgent.Utility;

public class ExceptionInterceptor(ILogger<ExceptionInterceptor> logger) : Interceptor
{
    private readonly ILogger<ExceptionInterceptor> _logger = logger;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
    }
}