using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TreeTools.Threading;

namespace TreeTools.Lifecycle;

public sealed class FeatureLifetime : IDisposable, IAsyncDisposable
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<Cleanup> disposers = [];
    private readonly List<Action> stoppingActions = [];
    private readonly HashSet<Task> tasks = [];
    private readonly List<Task> startupTasks = [];
    private readonly List<Exception> errors = [];
    private Task? stoppingTask;
    private Task? disposalTask;
    private bool startupClosed;
    private bool stopping;
    private bool disposed;

    public FeatureLifetime() => CancellationToken = cancellation.Token;

    public CancellationToken CancellationToken { get; }

    public bool IsStopping
    {
        get
        {
            lock (syncRoot)
                return stopping;
        }
    }

    public void Add(Action disposer)
    {
        ArgumentNullException.ThrowIfNull(disposer);
        lock (syncRoot)
        {
            ThrowIfStopping();
            disposers.Add(new(disposer, null));
        }
    }

    public void Add(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Add(resource.Dispose);
    }

    /// <summary>停用时取消任务队列，在释放依赖之前等待实际退出。</summary>
    public void Add(TaskHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        lock (syncRoot)
        {
            ThrowIfStopping();
            stoppingActions.Add(helper.Dispose);
            tasks.Add(helper.Completion);
        }
        Observe(helper.Completion);
    }

    public void AddAsync(Func<ValueTask> disposer)
    {
        ArgumentNullException.ThrowIfNull(disposer);
        lock (syncRoot)
        {
            ThrowIfStopping();
            disposers.Add(new(null, disposer));
        }
    }

    public void OnStopping(Action stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        lock (syncRoot)
        {
            ThrowIfStopping();
            stoppingActions.Add(stop);
        }
    }

    public void Track(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (syncRoot)
        {
            ThrowIfStopping();
            tasks.Add(task);
        }

        Observe(task);
    }

    /// <summary>
    /// 登记必须在模块发布启用成功之前完成的初始化任务，并将其纳入退出等待。
    /// </summary>
    public void TrackStartup(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (syncRoot)
        {
            ThrowIfStopping();
            if (startupClosed)
                throw new InvalidOperationException("Startup task registration has closed.");
            tasks.Add(task);
            startupTasks.Add(task);
        }
    }

    public async Task WaitForStartupAsync()
    {
        Task[] pending;
        lock (syncRoot)
        {
            startupClosed = true;
            pending = [.. startupTasks];
            startupTasks.Clear();
        }
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            lock (syncRoot)
                foreach (var task in pending)
                    tasks.Remove(task);
        }
    }

    public void RunBackground(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task task;
        lock (syncRoot)
        {
            ThrowIfStopping();
            task = Task.Run(() => operation(CancellationToken), default);
            tasks.Add(task);
        }

        Observe(task);
    }

    public void Stop()
    {
        Action[] actions;
        TaskCompletionSource completion;
        lock (syncRoot)
        {
            if (stoppingTask is not null)
            {
                if (!stoppingTask.IsCompleted)
                    throw new InvalidOperationException("The lifetime is stopping asynchronously; await StopAsync().");
                return;
            }

            stopping = true;
            actions = [.. stoppingActions.AsEnumerable().Reverse()];
            stoppingActions.Clear();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stoppingTask = completion.Task;
        }

        foreach (var stop in actions)
            Capture(stop);
        Capture(cancellation.Cancel);
        completion.TrySetResult();
    }

    public Task StopAsync(Func<Action, Task>? dispatcher = null)
    {
        TaskCompletionSource completion;
        lock (syncRoot)
        {
            if (stoppingTask is not null)
                return stoppingTask;

            stopping = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stoppingTask = completion.Task;
        }

        _ = CompleteStopAsync(completion, dispatcher);
        return completion.Task;
    }

    public async Task DrainAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Task[] pending;
        lock (syncRoot)
            pending = [.. tasks];

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
        }

        foreach (var task in pending)
            ObserveCompletion(task);
    }

    public void Dispose()
    {
        TaskCompletionSource completion;
        lock (syncRoot)
        {
            if (disposed)
                return;
            if (disposalTask is not null || stoppingTask is { IsCompleted: false } ||
                tasks.Any(static task => !task.IsCompleted) ||
                disposers.Any(static disposer => disposer.Async is not null))
                throw new InvalidOperationException("The lifetime owns asynchronous work; await DisposeAsync().");

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
            stopping = true;
        }

        Stop();
        Task[] completed;
        Cleanup[] cleanup;
        lock (syncRoot)
        {
            completed = [.. tasks];
            cleanup = [.. disposers.AsEnumerable().Reverse()];
            disposers.Clear();
        }

        foreach (var task in completed)
            ObserveCompletion(task);
        foreach (var disposer in cleanup)
        {
            if (disposer.Sync is { } dispose)
                Capture(dispose);
        }

        lock (syncRoot)
        {
            disposed = true;
            cancellation.Dispose();
            if (errors.Count > 0)
            {
                var error = new AggregateException("One or more feature lifetime operations failed.", errors.ToArray());
                completion.TrySetException(error);
                _ = completion.Task.Exception;
                throw error;
            }

            completion.TrySetResult();
        }
    }

    public ValueTask DisposeAsync() => new(DisposeAsync(null));

    public Task DisposeAsync(Func<Action, Task>? dispatcher)
    {
        TaskCompletionSource completion;
        lock (syncRoot)
        {
            if (disposalTask is not null)
                return disposalTask;
            if (disposed)
                return Task.CompletedTask;

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
        }

        _ = CompleteDisposeAsync(completion, dispatcher);
        return completion.Task;
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion, Func<Action, Task>? dispatcher)
    {
        Action[] actions;
        lock (syncRoot)
        {
            actions = [.. stoppingActions.AsEnumerable().Reverse()];
            stoppingActions.Clear();
        }

        foreach (var stop in actions)
        {
            try
            {
                if (dispatcher is null)
                    stop();
                else
                    await dispatcher(stop).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (syncRoot)
                    errors.Add(exception);
            }
        }

        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (syncRoot)
                errors.Add(exception);
        }

        completion.TrySetResult();
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion, Func<Action, Task>? dispatcher)
    {
        try
        {
            await StopAsync(dispatcher).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            Cleanup[] cleanup;
            lock (syncRoot)
            {
                cleanup = [.. disposers.AsEnumerable().Reverse()];
                disposers.Clear();
            }

            foreach (var disposer in cleanup)
            {
                try
                {
                    if (disposer.Async is { } asyncDispose)
                        await asyncDispose().ConfigureAwait(false);
                    else if (disposer.Sync is { } dispose)
                    {
                        if (dispatcher is null)
                            dispose();
                        else
                            await dispatcher(dispose).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    lock (syncRoot)
                        errors.Add(exception);
                }
            }

            lock (syncRoot)
            {
                disposed = true;
                cancellation.Dispose();
                if (errors.Count > 0)
                    completion.TrySetException(new AggregateException("One or more feature lifetime operations failed.", errors.ToArray()));
                else
                    completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void Observe(Task task) =>
        _ = task.ContinueWith(ObserveCompletion, default,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void ObserveCompletion(Task task)
    {
        lock (syncRoot)
        {
            if (tasks.Remove(task) && task.Exception is { } exception)
                errors.AddRange(exception.InnerExceptions);
        }
    }

    private void Capture(Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception exception)
        {
            lock (syncRoot)
                errors.Add(exception);
        }
    }

    private void ThrowIfStopping()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stopping)
            throw new InvalidOperationException("The lifetime has stopped accepting work.");
    }

    private readonly record struct Cleanup(Action? Sync, Func<ValueTask>? Async);
}
