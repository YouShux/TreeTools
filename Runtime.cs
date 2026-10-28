using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Expansion;
using OmenTools;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game;
using OmenTools.OmenService.Abstractions;
using OmenTools.Threading;
using TreeTools.Threading;
using OmenTaskHelper = OmenTools.Threading.TaskHelper.TaskHelper;
using IObjectTable = OmenTools.Dalamud.Services.Game.Object.Abstractions.IObjectTable;
using IAetheryteList = OmenTools.Dalamud.Services.Game.UI.Abstractions.IAetheryteList;

namespace TreeTools;

public sealed partial class Runtime : IAsyncDisposable
{
    private static readonly object CurrentLock = new();
    private static Runtime? current;
    private static readonly MethodInfo RegisterServices = typeof(DService).GetMethod("InitializeServices", MEMBER_FLAGS)
        ?? throw new MissingMethodException(typeof(DService).FullName, "InitializeServices");
    private static readonly MethodInfo ResetServices = typeof(DService).GetMethod("ResetServiceState", MEMBER_FLAGS)
        ?? throw new MissingMethodException(typeof(DService).FullName, "ResetServiceState");
    private readonly DService state = DService.Instance();
    private readonly object lifecycleLock = new();
    private Task? serviceStartup;
    private Task? disposal;
    private bool expansionPrepared;

    private Runtime(IDalamudPluginInterface source)
    {
        Framework = source.GetService(typeof(IFramework)) as IFramework
            ?? throw new InvalidOperationException("Framework service is unavailable.");
        PluginLog = source.GetService(typeof(IPluginLog)) as IPluginLog
            ?? throw new InvalidOperationException("Plugin log service is unavailable.");
        ValidateServiceContract();
        PluginInterface = PluginInterfaceAdapter.Create(source);
    }

    internal static Runtime Current => Volatile.Read(ref current)
        ?? throw new InvalidOperationException("TreeTools Runtime has not been prepared.");

    internal IFramework Framework { get; }
    internal IPluginLog PluginLog { get; }

    public IDalamudPluginInterface PluginInterface { get; }

    /// <summary>准备宿主运行上下文；同一程序集上下文只允许一个活动宿主。</summary>
    public static Runtime Prepare(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        lock (CurrentLock)
        {
            if (current is not null)
                throw new InvalidOperationException("TreeTools already belongs to an active host.");
            var runtime = new Runtime(pluginInterface);
            TaskHelper.BeginHostLifetime();
            Volatile.Write(ref current, runtime);
            return runtime;
        }
    }

    /// <summary>按上游依赖顺序启动服务；排除策略由宿主提供。</summary>
    public Task StartServicesAsync(IEnumerable<Type> excludedServices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excludedServices);
        lock (lifecycleLock)
        {
            if (disposal is not null || servicesStopping is not null || TaskHelper.IsHostStopping)
                throw new InvalidOperationException("TreeTools Runtime is stopping.");
            if (serviceStartup is not null)
                throw new InvalidOperationException("Services have already entered their lifecycle.");
            return serviceStartup = StartCoreAsync(excludedServices.ToArray(), cancellationToken);
        }
    }

    /// <summary>关闭新的任务队列创建入口；现有任务由各自作用域及退出链停止。</summary>
    public void BeginShutdown() => TaskHelper.CloseHostLifetime();

    private async Task StartCoreAsync(Type[] excludedServices, CancellationToken cancellationToken)
    {
        if (DService.IsInitialized || DService.IsDisposed)
            throw new InvalidOperationException("Upstream services already belong to another lifecycle.");
        await Framework.RunOnFrameworkThread(() => PrepareServices(excludedServices, cancellationToken)).ConfigureAwait(false);
        await StartServicesAsync(Get<List<OmenServiceBase>>("ServiceOrder"), cancellationToken).ConfigureAwait(false);
        Set(nameof(DService.IsInitialized), true);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleLock)
            return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        BeginShutdown();
        List<Exception> errors = [];
        try
        {
            await StopServicesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        try
        {
            await Framework.RunOnFrameworkThread(() => PluginInterfaceAdapter.DisposeHooks(PluginInterface)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            lock (CurrentLock)
                if (ReferenceEquals(current, this))
                    Volatile.Write(ref current, null);
        }
        if (errors.Count > 0)
            throw new AggregateException("Runtime cleanup failed.", errors);
    }

    private void PrepareServices(Type[] excludedServices, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PluginInterface.Inject(state))
            throw new InvalidOperationException("Upstream service injection failed.");
        Invoke(ResetServices, state);
        Set(nameof(DService.PI), PluginInterface);
        Set(nameof(DService.UIBuilder), PluginInterface.UiBuilder);

        lifetime.AddAsync(() => ReleaseAsync<Action>(
        [
            () => Set(nameof(DService.IsInitialized), false),
            () => Set(nameof(DService.IsDisposed), true)
        ], static action => action()));
        lifetime.AddAsync(() => ReleaseAsync<Action>(
        [
            () =>
            {
                if (expansionPrepared)
                    ExpansionInterop.Uninitialize();
            }
        ], static action => action()));
        lifetime.AddAsync(() => ReleaseAsync<Action>(
        [
            () => Invoke(ResetServices, state),
            () => Throttler.Shared.Clear(),
            () => Set(nameof(DService.ObjectTable), null),
            () => Set(nameof(DService.AetheryteList), null),
            () => Get<Dictionary<Type, IOmenDalamudService>>("OmenDalamudServices").Clear(),
            () => Sheets.Worlds.Clear(),
            () => Sheets.CNWorlds.Clear()
        ], static action => action()));
        lifetime.AddAsync(async () =>
        {
            var hooks = Get<ConditionalWeakTable<AssemblyLoadContext, ConcurrentDictionary<IDalamudHook, byte>>>("Hooks");
            try
            {
                await ReleaseAsync(hooks.SelectMany(static pair => pair.Value.Keys)
                    .Where(static hook => !hook.IsDisposed).ToArray(), static hook => hook.Dispose()).ConfigureAwait(false);
            }
            finally
            {
                hooks.Clear();
            }
        });
        lifetime.AddAsync(async () =>
        {
            var patches = Get<ConcurrentDictionary<MemoryPatch, byte>>("MemoryPatches");
            try
            {
                await ReleaseAsync(patches.Keys.ToArray(), static patch => patch.Dispose()).ConfigureAwait(false);
            }
            finally
            {
                patches.Clear();
            }
        });
        lifetime.AddAsync(async () =>
        {
            var helpers = Get<ConcurrentDictionary<OmenTaskHelper, byte>>("TaskHelpers");
            await ReleaseAsync(helpers.Keys.ToArray(), static helper => helper.Dispose()).ConfigureAwait(false);
        });

        Set(nameof(DService.ObjectTable), CreateWrapper("OmenTools.Dalamud.Services.Game.Object.ObjectTable"));
        Set(nameof(DService.AetheryteList), CreateWrapper("OmenTools.Dalamud.Services.Game.UI.AetheryteList"));
        var services = Get<Dictionary<Type, IOmenDalamudService>>("OmenDalamudServices");
        services[typeof(IObjectTable)] = (IOmenDalamudService)state.ObjectTable;
        services[typeof(IAetheryteList)] = (IOmenDalamudService)state.AetheryteList;
        expansionPrepared = true;
        ExpansionInterop.Initialize(state.SigScanner.SearchBase);
        Invoke(RegisterServices, state, arguments: [excludedServices]);
    }

    private static void ValidateServiceContract()
    {
        foreach (var name in new[] { nameof(DService.IsInitialized), nameof(DService.IsDisposed), nameof(DService.PI),
                     nameof(DService.UIBuilder), nameof(DService.ObjectTable), nameof(DService.AetheryteList) })
            if (Property(name).GetSetMethod(true) is null)
                throw new MissingMethodException(typeof(DService).FullName, $"set_{name}");
        foreach (var name in new[] { "ServiceOrder", "Hooks", "MemoryPatches", "TaskHelpers", "OmenDalamudServices" })
            _ = Property(name);
        if (ServiceInitialized.GetSetMethod(true) is null || ServiceDisposed.GetSetMethod(true) is null)
            throw new MissingMethodException("Upstream service state setters are unavailable.");
    }

    private static object CreateWrapper(string name) =>
        Activator.CreateInstance(typeof(DService).Assembly.GetType(name) ?? throw new TypeLoadException(name), true)
        ?? throw new InvalidOperationException($"Service wrapper {name} could not be constructed.");

    private T Get<T>(string name) => Property(name).GetValue(state) is T value
        ? value
        : throw new InvalidOperationException($"Service member {name} has an incompatible value.");

    private void Set(string name, object? value) => Property(name).SetValue(state, value);

    private static PropertyInfo Property(string name) => typeof(DService).GetProperty(name, MEMBER_FLAGS)
        ?? throw new MissingMemberException(typeof(DService).FullName, name);
}
