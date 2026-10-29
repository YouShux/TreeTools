using System.Threading;
using System.Threading.Tasks;

namespace TreeTools.Threading;

/// <summary>由任务队列拥有的单项任务与退出信号。</summary>
public sealed class TaskHelperTask
{
    /// <summary>同步重试操作，返回 true 表示完成。</summary>
    public Func<bool>? Action { get; }

    /// <summary>异步重试操作，队列等待实际返回的任务。</summary>
    public Func<CancellationToken, Task<bool>>? AsyncAction { get; }

    /// <summary>本项超时毫秒数，非正数时使用队列设置。</summary>
    public int TimeoutMS { get; }

    /// <summary>本项超时处理方式，未设置时使用队列设置。</summary>
    public TaskAbortBehaviour? TimeoutBehaviour { get; }

    /// <summary>本项异常处理方式，未设置时使用队列设置。</summary>
    public TaskAbortBehaviour? ExceptionBehaviour { get; }

    /// <summary>实际执行结束后在主线程调用的超时回调。</summary>
    public Action? TimeoutAction { get; set; }

    /// <summary>在主线程调用的异常回调。</summary>
    public Action? ExceptionAction { get; set; }

    internal CancellationTokenSource CancelToken { get; } = new();

    /// <summary>任务显示名称。</summary>
    public string Name { get; }

    internal long StartTime { get; set; }

    /// <summary>创建同步任务，返回 false 时等待下一 Tick 重试。</summary>
    public TaskHelperTask
    (
        Func<bool>          action,
        string?             name = null,
        int                 timeoutMS = 0,
        TaskAbortBehaviour? timeoutBehaviour = null,
        TaskAbortBehaviour? exceptionBehaviour = null,
        Action?             timeoutAction = null,
        Action?             exceptionAction = null
    )
    {
        ArgumentNullException.ThrowIfNull(action);
        Action             = action;
        Name               = name ?? "(无名称)";
        TimeoutMS          = timeoutMS;
        TimeoutBehaviour   = timeoutBehaviour;
        ExceptionBehaviour = exceptionBehaviour;
        TimeoutAction      = timeoutAction;
        ExceptionAction    = exceptionAction;
    }

    /// <summary>创建异步任务，必须结束实际工作后才能完成退出。</summary>
    public TaskHelperTask
    (
        Func<CancellationToken, Task<bool>> asyncAction,
        string?                             name = null,
        int                                 timeoutMS = 0,
        TaskAbortBehaviour?                 timeoutBehaviour = null,
        TaskAbortBehaviour?                 exceptionBehaviour = null,
        Action?                             timeoutAction = null,
        Action?                             exceptionAction = null
    )
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        AsyncAction        = asyncAction;
        Name               = name ?? "(无名称)";
        TimeoutMS          = timeoutMS;
        TimeoutBehaviour   = timeoutBehaviour;
        ExceptionBehaviour = exceptionBehaviour;
        TimeoutAction      = timeoutAction;
        ExceptionAction    = exceptionAction;
    }

    /// <summary>获取用于队列状态及日志的名称。</summary>
    public string GetName() => Name;
}
