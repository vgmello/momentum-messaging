namespace Momentum.Messaging;

public interface IRequest<out TResponse>;
public interface IRequest : IRequest<Unit>;
public interface INotification;

public readonly record struct Unit
{
    public static readonly Unit Value = new();
    public static readonly Task<Unit> Task = System.Threading.Tasks.Task.FromResult(Value);
}
