namespace Momentum.Messaging;

public delegate Task<TResponse> NextDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, NextDelegate<TResponse> next, CancellationToken ct = default);
}
