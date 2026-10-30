using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TreeTools.Threading;

public static class FrameworkWorkScheduler
{
    public static async Task RunAsync<T>
    (
        IReadOnlyList<T>  items,
        Action<T>         operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);
        if (items.Count == 0)
            return;

        var framework = Runtime.Current.Framework;
        var index = 0;
        while (index < items.Count)
        {
            await framework.Run(() =>
            {
                var startedAt = Stopwatch.GetTimestamp();
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operation(items[index++]);
                }
                while (index < items.Count && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < 1);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
