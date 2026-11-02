using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TreeTools.Threading;

public static class FrameworkWorkScheduler
{
    private static readonly AsyncLocal<FrameBudget?> PipelineBudget = new();

    /// <summary>按原顺序在框架线程执行工作项，约 1 毫秒后将剩余工作让到后续帧，并等待全部完成。</summary>
    public static Task RunAsync<T>
    (
        IReadOnlyList<T>  items,
        Action<T>         operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);
        if (items.Count == 0)
            return Task.CompletedTask;

        var framework = Runtime.Current.Framework;
        var budget = PipelineBudget.Value;
        return RunAsync(async () =>
        {
            var index = 0;
            while (index < items.Count)
            {
                var startedAt = budget?.GetStartedAt(framework.LastUpdateUTC.Ticks) ?? Stopwatch.GetTimestamp();
                if (budget is not null && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= 1)
                {
                    await framework.DelayTicks(1, cancellationToken);
                    continue;
                }
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operation(items[index++]);
                }
                while (index < items.Count && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < 1);
                if (index < items.Count)
                    await framework.DelayTicks(1, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>在框架线程依次完成各阶段，其数组工作跨阶段共用约 1 毫秒的每帧预算。</summary>
    public static Task RunPipelineAsync
    (
        IReadOnlyList<Func<Task>> stages,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stages);
        return RunAsync(async () =>
        {
            var framework = Runtime.Current.Framework;
            var previous = PipelineBudget.Value;
            var budget = previous ?? new FrameBudget();
            PipelineBudget.Value = budget;
            try
            {
                foreach (var stage in stages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Stopwatch.GetElapsedTime(budget.GetStartedAt(framework.LastUpdateUTC.Ticks)).TotalMilliseconds >= 1)
                        await framework.DelayTicks(1, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await stage();
                }
            }
            finally
            {
                PipelineBudget.Value = previous;
            }
        }, cancellationToken);
    }

    internal static Task RunAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        var framework = Runtime.Current.Framework;
        return framework.IsInFrameworkUpdateThread && TaskScheduler.Current == framework.GetTaskFactory().Scheduler
            ? operation()
            : framework.Run(operation, cancellationToken);
    }

    /// <summary>在框架线程按整条流水线共用的 3 毫秒预算执行，每批开始前校验状态。</summary>
    internal static Task RunAsync
    (
        IEnumerable<Action> operations,
        Action beforeBatch,
        CancellationToken cancellationToken
    ) => RunAsync(async () =>
    {
        var framework = Runtime.Current.Framework;
        cancellationToken.ThrowIfCancellationRequested();
        using var iterator = operations.GetEnumerator();
        var hasNext = iterator.MoveNext();
        while (hasNext)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeBatch();
            var startedAt = Stopwatch.GetTimestamp();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterator.Current();
                hasNext = iterator.MoveNext();
            }
            while (hasNext && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < 3);
            if (hasNext)
                await framework.DelayTicks(1, cancellationToken);
        }
    }, cancellationToken);

    private sealed class FrameBudget
    {
        private long frame;
        private long startedAt;

        internal long GetStartedAt(long currentFrame)
        {
            if (frame != currentFrame)
            {
                frame = currentFrame;
                startedAt = Stopwatch.GetTimestamp();
            }
            return startedAt;
        }
    }
}
