namespace Momentum.Messaging;

public interface IMessageBus
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
    Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification;
}

public interface INotificationPublishStrategy
{
    Task PublishAsync<TNotification>(
        IReadOnlyList<Func<TNotification, CancellationToken, Task>> handlers,
        TNotification notification,
        CancellationToken ct)
        where TNotification : INotification;
}

public sealed class SequentialStrategy : INotificationPublishStrategy
{
    public static readonly SequentialStrategy Instance = new();

    public async Task PublishAsync<TNotification>(
        IReadOnlyList<Func<TNotification, CancellationToken, Task>> handlers,
        TNotification notification,
        CancellationToken ct)
        where TNotification : INotification
    {
        for (var i = 0; i < handlers.Count; i++)
            await handlers[i](notification, ct).ConfigureAwait(false);
    }
}

public sealed class ParallelStrategy : INotificationPublishStrategy
{
    public static readonly ParallelStrategy Instance = new();

    public async Task PublishAsync<TNotification>(
        IReadOnlyList<Func<TNotification, CancellationToken, Task>> handlers,
        TNotification notification,
        CancellationToken ct)
        where TNotification : INotification
    {
        var tasks = new Task[handlers.Count];
        for (var i = 0; i < handlers.Count; i++)
            tasks[i] = handlers[i](notification, ct);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
