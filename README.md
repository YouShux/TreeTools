# TreeTools

TreeTools 负责任务、生命周期、主线程调度和通用计算，让插件与模块遵循同一套接入和退出规则。

| 项目 | 当前值 |
| --- | --- |
| 版本 | `1.0.0` |
| 目标 | .NET 10 Windows、Dalamud API 15 |
| 源码依赖 | OmenTools、FFXIVClientStructs.Expansion |
| Omni 中用途 | Common、本体模块、本地模块、在线模块 |

## 能力地图

| 命名空间 | 解决的问题 |
| --- | --- |
| `TreeTools.Threading` | 有界任务队列、取消、超时、重试和跨帧调度 |
| `TreeTools.Lifecycle` | 单次启用作用域、Hook、Addon 事件和逆序释放 |
| `TreeTools.IO` | 防止半写入配置的原子文件操作 |
| `TreeTools.Serialization` | 当前加载上下文的共享 JSON 元数据引用检查与最终释放 |
| `TreeTools.Reflection` | 所有代理使用者停止后释放本加载上下文的动态代理缓存引用 |
| `TreeTools.Data` | 查表任务停止后释放共享 Lumina 缓存中的本加载上下文行类型元数据 |
| `TreeTools.Text` | 简繁转换与拼音搜索文本 |

`FrameworkWorkScheduler.RunAsync` 按原顺序执行工作项，约 1 毫秒后将剩余工作让到后续帧；`RunPipelineAsync` 让多个阶段共享每帧预算。需要统计处理耗时时，使用带 `Action<TimeSpan>` 的四参数重载，在每批结束后接收批内处理时间，包含循环和取消检查，失败或取消也报告已执行批次。统计回调在框架线程同步执行，应保持轻量。

## 数据维护工具

在 Omni 工作区执行 `dotnet build lib/TreeTools/TreeTools.csproj -p:TreeToolsGameDataTool=true -c Debug --artifacts-path <仓库外目录>`，随后通过 `dotnet <仓库外目录>/bin/TreeTools/debug/TreeTools.dll <命令及参数>` 调用 `generate`、`verify` 或 `export`。工具模式依赖 Omni Common 源码。

## 接入宿主

```csharp
using Dalamud.Plugin;
using TreeTools;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private Runtime? runtime;

    public string Name => "MyPlugin";

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        runtime = Runtime.Prepare(pluginInterface);
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return runtime!.StartServicesAsync([], cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (runtime is null)
            return;

        await runtime.StopTaskHelpersAsync();
        await runtime.DisposeAsync();
    }
}
```

退出时先停止业务入口，再等待全部 TreeTools 任务结束；UI、服务和其他依赖应在之后释放。

## 托管模块任务

```csharp
using TreeTools.Lifecycle;
using TreeTools.Threading;

protected override void OnStart(FeatureLifetime activation)
{
    var tasks = new TaskHelper { TimeoutMS = 0 };
    activation.Add(tasks);

    tasks.EnqueueAsync(async token =>
    {
        await DoWorkAsync(token);
    });
}
```

`Dispose()` 发起取消，`Completion` 和 `DisposeAsync()` 表示实际结束。`activation.Add(tasks)` 会把这段等待纳入模块停用流程。

## 依赖边界

| 使用场景 | 需要做什么 |
| --- | --- |
| 直接在 Omni 中运行 | Omni 已携带 TreeTools、OmenTools 和 FFCS，无需单独安装 |
| 在线 `.cs` 模块 | 由 Omni 提供编译引用，不需要写 csproj 或打包 DLL |
| 在线 `.dll` 模块 | 使用与当前 Omni 一致的依赖编译，不把 TreeTools、OmenTools 等共享库打进模块 |
| 独立插件使用 `Runtime` | 当前版必须同时提供 OmenTools 和 FFCS；它不是零依赖运行库 |

OmenTools 与 FFXIVClientStructs.Expansion 保持上游固定提交，不在子模块内维护 Omni 专用修改。需要补充 Omen 服务顺序、任务退出、网络、互操作或游戏数据行为时，在 TreeTools 内通过包装、扩展或适配控制器实现；原生 UI 相关适配放入 TreeToolKit。确需修改上游时，先提交正式上游 PR 并固定合并提交。
