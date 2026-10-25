using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TreeTools.Threading;

/// <summary>
/// 串行执行主线程任务，并等待异步任务和取消回调实际结束。
/// </summary>
public partial class TaskHelper : IDisposable, IAsyncDisposable
{
    private static readonly object HostLock = new();
    private static readonly HashSet<TaskHelper> Active = [];
    private static readonly List<(int TaskID, string Reason)> HostFailures = [];
    private static bool HostStopping;
    private static Task? HostExit;
    private readonly object syncRoot = new();
    private readonly Runtime runtime;
    private readonly SortedDictionary<int, LinkedList<Item>> queues = new(Comparer<int>.Create(static (left, right) => right.CompareTo(left)));
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> retired = [];
    private readonly List<Exception> cleanupErrors = [];
    private Task cancellation = Task.CompletedTask;
    private Item? current;
    private bool disposed;
    private bool busy;

    /// <summary>创建任务队列，并登记宿主退出等待。</summary>
    public TaskHelper()
    {
        lock (HostLock)
        {
            if (HostStopping)
                throw new InvalidOperationException("宿主正在退出，不能创建 TaskHelper。");
            runtime = Runtime.Current;
            queues.Add(1, []);
            queues.Add(0, []);
            Completion = Task.Run(ProcessAsync);
            Active.Add(this);
            _ = Completion.ContinueWith(static (_, state) =>
            {
                lock (HostLock)
                    Active.Remove((TaskHelper)state!);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>任务循环、所有已启动任务及取消回调完成后结束。</summary>
    public Task Completion { get; }

    internal static bool IsHostStopping
    {
        get
        {
            lock (HostLock)
                return HostStopping;
        }
    }

    /// <summary>当前任务名称。</summary>
    public string CurrentTaskName
    {
        get
        {
            lock (syncRoot)
                return current?.Task.GetName() ?? string.Empty;
        }
    }

    /// <summary>排队任务和仍在执行的任务数量。</summary>
    public int CurrentTasksCount
    {
        get
        {
            lock (syncRoot)
                return queues.Values.Sum(static queue => queue.Count) + (current is null ? 0 : 1);
        }
    }

    /// <summary>是否还有排队或正在执行的任务。</summary>
    public bool IsBusy => CurrentTasksCount > 0;

    /// <summary>进入繁忙状态时在主线程触发。</summary>
    public event Action? EnterBusyState;

    /// <summary>离开繁忙状态时在主线程触发。</summary>
    public event Action? LeaveBusyState;

    /// <summary>未被单个任务覆盖时使用的超时处理方式。</summary>
    public TaskAbortBehaviour TimeoutBehaviour { get; set; } = TaskAbortBehaviour.AbortAll;

    /// <summary>未被单个任务覆盖时使用的异常处理方式。</summary>
    public TaskAbortBehaviour ExceptionBehaviour { get; set; } = TaskAbortBehaviour.AbortAll;

    /// <summary>任务超时且实际执行结束后在主线程触发。</summary>
    public event Action<TaskHelperTask>? TaskTimeout;

    /// <summary>任务发生异常后在主线程触发。</summary>
    public event Action<TaskHelperTask>? TaskGetException;

    /// <summary>开始下一任务之前在主线程检查的条件。</summary>
    public Func<bool>? MoveToNextCheckFunc { get; set; }

    /// <summary>是否输出任务调试信息。</summary>
    public bool ShowDebug { get; set; }

    /// <summary>默认超时毫秒数；非正数表示不设置超时。</summary>
    public int TimeoutMS { get; set; } = 10_000;

    /// <summary>重试间隔毫秒数；每次重试至少等待一个 Tick。</summary>
    public int RetryIntervalMS { get; set; }

    /// <summary>任务间隔毫秒数；后续任务至少等待一个 Tick。</summary>
    public int TaskIntervalMS { get; set; }

    /// <summary>是否已关闭入口；实际退出应等待 Completion。</summary>
    public bool IsDisposed
    {
        get
        {
            lock (syncRoot)
                return disposed;
        }
    }

    /// <summary>开始新的宿主生命周期，禁止与尚未退出的宿主重叠。</summary>
    internal static void BeginHostLifetime()
    {
        lock (HostLock)
        {
            if (HostExit is { IsCompleted: false } || Active.Any(static helper => !helper.Completion.IsCompleted))
                throw new InvalidOperationException("上一宿主的 TaskHelper 尚未退出。");
            if (HostFailures.Count > 0)
                throw new InvalidOperationException("上一宿主的 TaskHelper 清理失败，必须先完成宿主退出。");
            Active.Clear();
            HostExit = null;
            HostStopping = false;
        }
    }

    /// <summary>关闭创建入口，停止所有任务队列，并等待实际退出。</summary>
    internal static void CloseHostLifetime()
    {
        lock (HostLock)
            HostStopping = true;
    }

    internal static Task StopAllAsync()
    {
        lock (HostLock)
        {
            HostStopping = true;
            return HostExit ??= CompleteHostExitAsync([.. Active]);
        }
    }

    private static async Task CompleteHostExitAsync(TaskHelper[] helpers)
    {
        foreach (var helper in helpers)
            helper.Dispose();
        try
        {
            await Task.WhenAll(helpers.Select(static helper => helper.Completion)).ConfigureAwait(false);
        }
        catch
        {
        }
        var errors = helpers.Select(static helper => helper.Completion.Exception).OfType<AggregateException>()
            .SelectMany(static exception => exception.InnerExceptions).ToList();
        var awaited = helpers.Select(static helper => helper.Completion.Id).ToHashSet();
        lock (HostLock)
        {
            errors.AddRange(HostFailures.Where(failure => !awaited.Contains(failure.TaskID))
                .Select(static failure => new InvalidOperationException(failure.Reason)));
            HostFailures.Clear();
        }
        if (errors.Count > 0)
            throw new AggregateException("TaskHelper 宿主退出失败。", errors);
    }

    /// <summary>关闭入口并发送取消；使用 DisposeAsync 等待实际退出。</summary>
    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
                return;
            disposed = true;
            AbortLocked();
            cancellation = stopping.CancelAsync();
            wake.Writer.TryComplete();
        }
    }

    /// <summary>等待所有实际任务、取消回调和调度回调结束。</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }

    /// <summary>取消当前任务并清空队列；后续任务等待当前执行实际结束。</summary>
    public void Abort()
    {
        lock (syncRoot)
        {
            if (!disposed)
                AbortLocked();
        }
        wake.Writer.TryWrite(0);
    }

    /// <summary>创建指定权重的队列。</summary>
    public void AddQueue(int weight)
    {
        lock (syncRoot)
            if (!disposed)
                queues.TryAdd(weight, []);
    }

    /// <summary>移除队列及尚未执行的任务。</summary>
    public void RemoveQueue(int weight) => RemoveTasks(weight, int.MaxValue, false, true);

    /// <summary>清空队列中尚未执行的任务。</summary>
    public void RemoveQueueTasks(int weight) => RemoveTasks(weight, int.MaxValue, false, false);

    /// <summary>移除队列中第一个尚未执行的任务。</summary>
    public void RemoveQueueFirstTask(int weight) => RemoveTasks(weight, 1, false, false);

    /// <summary>移除队列中最后一个尚未执行的任务。</summary>
    public void RemoveQueueLastTask(int weight) => RemoveTasks(weight, 1, true, false);

    /// <summary>移除队列中最前面的指定数量任务。</summary>
    public void RemoveQueueFirstNTasks(int weight, int count) => RemoveTasks(weight, count, false, false);

    /// <summary>取得指定队列中尚未执行的任务数量。</summary>
    public int GetQueueTaskCount(int weight)
    {
        lock (syncRoot)
            return queues.TryGetValue(weight, out var queue) ? queue.Count : 0;
    }

    private void EnqueueCore(Func<TaskHelperTask> factory, int weight)
    {
        lock (syncRoot)
        {
            if (disposed)
                return;
            if (!queues.TryGetValue(weight, out var queue))
                queues.Add(weight, queue = []);
            queue.AddLast(new Item(factory()));
        }
        wake.Writer.TryWrite(0);
    }

    private void RemoveTasks(int weight, int count, bool last, bool removeQueue)
    {
        lock (syncRoot)
        {
            if (disposed || !queues.TryGetValue(weight, out var queue))
                return;
            while (count-- > 0 && queue.Count > 0)
            {
                var node = (last ? queue.Last : queue.First) ?? throw new InvalidOperationException("TaskHelper 队列状态无效。");
                queue.Remove(node);
                RetireLocked(node.Value, true);
            }
            if (removeQueue)
                queues.Remove(weight);
            ObserveRetiredLocked();
        }
    }

    private void AbortLocked()
    {
        if (current is not null)
            CancelLocked(current, true);
        foreach (var queue in queues.Values)
        {
            foreach (var item in queue)
                RetireLocked(item, true);
            queue.Clear();
        }
        ObserveRetiredLocked();
    }

    private static void CancelLocked(Item item, bool suppress)
    {
        item.Suppressed |= suppress;
        if (item.Canceled)
            return;
        item.Canceled = true;
        item.Cancellation = item.Task.CancelToken.CancelAsync();
    }

    private void RetireLocked(Item item, bool cancel)
    {
        if (cancel)
            CancelLocked(item, true);
        retired.Add(DisposeTokenAsync(item));
    }

    private static async Task DisposeTokenAsync(Item item)
    {
        try
        {
            await item.Cancellation.ConfigureAwait(false);
        }
        finally
        {
            item.Task.CancelToken.Dispose();
        }
    }

    private void ObserveRetiredLocked()
    {
        for (var index = retired.Count - 1; index >= 0; index--)
        {
            var task = retired[index];
            if (!task.IsCompleted)
                continue;
            if (task.Exception is not null)
                cleanupErrors.AddRange(task.Exception.InnerExceptions);
            retired.RemoveAt(index);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (!IsDisposed)
            {
                Item? item;
                bool hasWork;
                lock (syncRoot)
                {
                    ObserveRetiredLocked();
                    item = current;
                    hasWork = item is not null || queues.Values.Any(static queue => queue.Count > 0);
                }
                await SetBusyAsync(hasWork).ConfigureAwait(false);
                if (!hasWork)
                {
                    if (!await wake.Reader.WaitToReadAsync(stopping.Token).ConfigureAwait(false))
                        break;
                    while (wake.Reader.TryRead(out _))
                    {
                    }
                    continue;
                }

                if (item is null)
                {
                    await DispatchAsync(() =>
                    {
                        if (IsDisposed || MoveToNextCheckFunc?.Invoke() is false)
                            return;
                        lock (syncRoot)
                        {
                            if (disposed)
                                return;
                            var queue = queues.Values.FirstOrDefault(static queue => queue.Count > 0);
                            if (queue?.First is { } first)
                            {
                                item = current = first.Value;
                                queue.RemoveFirst();
                            }
                        }
                    }).ConfigureAwait(false);
                    if (item is null)
                    {
                        await runtime.Framework.DelayTicks(1, stopping.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                var complete = await ExecuteAsync(item).ConfigureAwait(false);
                lock (syncRoot)
                {
                    if (complete || item.Canceled)
                    {
                        RetireLocked(item, false);
                        current = null;
                    }
                }
                if (IsBusy && !IsDisposed)
                {
                    var interval = complete ? TaskIntervalMS : RetryIntervalMS;
                    if (interval > 0)
                        await Task.Delay(interval, stopping.Token).ConfigureAwait(false);
                    await runtime.Framework.DelayTicks(1, stopping.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            lock (syncRoot)
                cleanupErrors.Add(exception);
        }
        finally
        {
            Dispose();
            Task[] pending;
            lock (syncRoot)
            {
                if (current is not null)
                {
                    RetireLocked(current, false);
                    current = null;
                }
                pending = [.. retired, cancellation];
            }
            foreach (var task in pending)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }
            lock (syncRoot)
            {
                retired.Clear();
                queues.Clear();
            }
            stopping.Dispose();
            EnterBusyState = null;
            LeaveBusyState = null;
            TaskTimeout = null;
            TaskGetException = null;
            MoveToNextCheckFunc = null;
        }
        if (cleanupErrors.Count > 0)
        {
            var error = new AggregateException("TaskHelper 退出清理失败。", cleanupErrors);
            lock (HostLock)
                HostFailures.Add((Completion.Id, error.ToString()));
            throw error;
        }
    }

    private async Task<bool> ExecuteAsync(Item item)
    {
        var timeout = item.Task.TimeoutMS > 0 ? item.Task.TimeoutMS : TimeoutMS;
        var timedOut = item.Started && timeout > 0 && Stopwatch.GetElapsedTime(item.Task.StartTime).TotalMilliseconds >= timeout;
        Exception? error = null;
        var complete = false;
        try
        {
            Task<bool>? execution = null;
            if (!timedOut)
            {
                await DispatchAsync(() =>
                {
                    lock (syncRoot)
                        if (disposed || item.Canceled)
                            return;
                    if (!item.Started)
                    {
                        item.Started = true;
                        item.Task.StartTime = Stopwatch.GetTimestamp();
                        if (ShowDebug)
                            runtime.PluginLog.Debug($"TaskHelper 开始任务 {item.Task.GetName()}。");
                    }
                    execution = item.Task.AsyncAction is { } asyncAction
                        ? asyncAction(item.Task.CancelToken.Token)
                        : Task.FromResult((item.Task.Action ?? throw new InvalidOperationException("任务缺少执行逻辑。"))());
                }).ConfigureAwait(false);
                if (execution is null)
                    return false;
                if (timeout > 0 && !execution.IsCompleted)
                {
                    using var timer = new CancellationTokenSource();
                    var remaining = Math.Max(1, timeout - Stopwatch.GetElapsedTime(item.Task.StartTime).TotalMilliseconds);
                    var deadline = Task.Delay(TimeSpan.FromMilliseconds(remaining), timer.Token);
                    if (await Task.WhenAny(execution, deadline).ConfigureAwait(false) == deadline)
                    {
                        lock (syncRoot)
                        {
                            timedOut = !item.Suppressed;
                            CancelLocked(item, false);
                        }
                    }
                    await timer.CancelAsync().ConfigureAwait(false);
                }
                complete = await execution.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (item.Canceled)
        {
        }
        catch (Exception exception)
        {
            error = exception;
        }

        if ((timedOut || error is not null) && !item.Suppressed && !IsDisposed)
        {
            if (timedOut)
                lock (syncRoot)
                    CancelLocked(item, false);
            if (error is not null)
                runtime.PluginLog.Error(error, $"TaskHelper 任务 {item.Task.GetName()} 执行失败。");
            var behaviour = timedOut
                ? item.Task.TimeoutBehaviour ?? TimeoutBehaviour
                : item.Task.ExceptionBehaviour ?? ExceptionBehaviour;
            lock (syncRoot)
            {
                if (behaviour == TaskAbortBehaviour.AbortAll)
                    AbortLocked();
            }
            await DispatchAsync(() =>
            {
                if (IsDisposed)
                    return;
                if (timedOut)
                {
                    item.Task.TimeoutAction?.Invoke();
                    TaskTimeout?.Invoke(item.Task);
                }
                else
                {
                    item.Task.ExceptionAction?.Invoke();
                    TaskGetException?.Invoke(item.Task);
                }
            }).ConfigureAwait(false);
        }
        return complete || timedOut || error is not null;
    }

    private Task SetBusyAsync(bool value)
    {
        if (busy == value)
            return Task.CompletedTask;
        busy = value;
        return DispatchAsync(() =>
        {
            if (!IsDisposed)
            {
                if (value)
                    EnterBusyState?.Invoke();
                else
                    LeaveBusyState?.Invoke();
            }
        });
    }

    private Task DispatchAsync(Action action) => runtime.Framework.Run(action, cancellationToken: CancellationToken.None);

    private sealed class Item(TaskHelperTask task)
    {
        internal TaskHelperTask Task { get; } = task;
        internal Task Cancellation { get; set; } = System.Threading.Tasks.Task.CompletedTask;
        internal bool Canceled { get; set; }
        internal bool Suppressed { get; set; }
        internal bool Started { get; set; }
    }
}
