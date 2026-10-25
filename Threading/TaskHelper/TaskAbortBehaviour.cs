namespace TreeTools.Threading;

/// <summary>任务超时或异常后的队列处理方式。</summary>
public enum TaskAbortBehaviour
{
    /// <summary>中断当前任务并清空尚未执行的任务。</summary>
    AbortAll,

    /// <summary>中断当前任务，继续处理后续任务。</summary>
    AbortCurrent
}
