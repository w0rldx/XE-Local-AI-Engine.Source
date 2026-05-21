namespace XE_Local_AI_Engine.HostAgent.Linux.Security;

using global::Grpc.Core;
using global::Grpc.Core.Interceptors;

public sealed class HmacAuthenticationInterceptor : Interceptor
{
    private readonly HmacRequestValidator _validator;

    public HmacAuthenticationInterceptor(HmacRequestValidator validator)
    {
        _validator = validator;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Validate(request, context);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Validate(request, context);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    private void Validate<TRequest>(TRequest request, ServerCallContext context) where TRequest : class
    {
        var result = _validator.Validate(request, context.RequestHeaders, context.Method);
        if (!result.Succeeded)
        {
            throw new RpcException(new Status(result.StatusCode, result.Detail));
        }
    }
}
