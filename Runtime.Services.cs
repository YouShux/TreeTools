using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using OmenTools.OmenService;
using OmenTools.OmenService.Abstractions;
using TreeTools.Lifecycle;
using TreeTools.Threading;

namespace TreeTools;

public sealed partial class Runtime
{
    private static readonly Action<OmenServiceBase> InitializeService = typeof(OmenServiceBase).GetMethod("Init", MEMBER_FLAGS)
        ?.CreateDelegate<Action<OmenServiceBase>>()
        ?? throw new MissingMethodException(typeof(OmenServiceBase).FullName, "Init");
    private static readonly Action<OmenServiceBase> UninitializeService = typeof(OmenServiceBase).GetMethod("Uninit", MEMBER_FLAGS)
        ?.CreateDelegate<Action<OmenServiceBase>>()
        ?? throw new MissingMethodException(typeof(OmenServiceBase).FullName, "Uninit");
    private static readonly PropertyInfo   ServiceInitialized  = typeof(OmenServiceBase).GetProperty("IsInitialized", MEMBER_FLAGS)
        ?? throw new MissingMemberException(typeof(OmenServiceBase).FullName, "IsInitialized");
    private static readonly PropertyInfo   ServiceDisposed     = typeof(OmenServiceBase).GetProperty("IsDisposed", MEMBER_FLAGS)
        ?? throw new MissingMemberException(typeof(OmenServiceBase).FullName, "IsDisposed");
    private static readonly Func<string[]> DiscoverDataShareTags = typeof(DataShareManager).GetMethod("DiscoverDataShareTags", MEMBER_FLAGS)
        ?.CreateDelegate<Func<string[]>>()
        ?? throw new MissingMethodException(typeof(DataShareManager).FullName, "DiscoverDataShareTags");
    private static readonly FieldInfo DataShareTags = typeof(DataShareManager).GetField("dataShareTags", MEMBER_FLAGS)
        ?? throw new MissingFieldException(typeof(DataShareManager).FullName, "dataShareTags");
    private readonly FeatureLifetime       lifetime            = new();
    private readonly List<OmenServiceBase> started             = [];
    private Task?                          helpersStopping;
    private Task?                          servicesStopping;

    internal Task StartServicesAsync
    (
        IReadOnlyList<OmenServiceBase> services,
        CancellationToken              cancellationToken
    )
    {
        var prepareAetherytes = services.Any(service => service.GetType() == typeof(AetheryteRecordManager));
        var prepareIPC = services.Any(service => service.GetType() == typeof(IPCManager));
        var prepareDataShare = services.SkipWhile(service => service.GetType() != typeof(AetheryteRecordManager))
            .Any(service => service.GetType() == typeof(DataShareManager));
        string[]? preparedDataShareTags = null;
        var startupCode = prepareAetherytes || prepareIPC ? Task.Run(() =>
        {
            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (service.GetType() == typeof(AetheryteRecordManager))
                    continue;
                var method = service.GetType().GetMethod("Init", MEMBER_FLAGS)
                    ?? throw new MissingMethodException(service.GetType().FullName, "Init");
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (prepareDataShare)
                preparedDataShareTags = DiscoverDataShareTags();
            if (prepareIPC)
                RuntimeHelpers.RunClassConstructor(typeof(IPCManager).TypeHandle);
            if (prepareAetherytes)
                PrepareAetheryteCode(cancellationToken);
        }, cancellationToken) : Task.CompletedTask;
        lifetime.Track(startupCode);
        return FrameworkWorkScheduler.RunAsync(async () =>
        {
            lifetime.AddAsync(StopStartedServicesAsync);
            List<OmenServiceBase> pending = [];
            foreach (var service in services)
            {
                if (service.GetType() != typeof(AetheryteRecordManager))
                {
                    pending.Add(service);
                    continue;
                }
                await FrameworkWorkScheduler.RunAsync(pending, Initialize, cancellationToken);
                pending.Clear();
                await startupCode;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Begin(service);
                    await BuildAetheryteRecordsAsync((AetheryteRecordManager)service, cancellationToken);
                    ServiceInitialized.SetValue(service, true);
                }
                catch (Exception exception)
                {
                    PluginLog.Error(exception,
                        "Omen service {ServiceName} failed to initialize.", service.GetType().Name);
                    throw;
                }
            }
            await FrameworkWorkScheduler.RunAsync(pending, Initialize, cancellationToken);
            await startupCode;

            void Begin(OmenServiceBase service)
            {
                if (ServiceInitialized.GetValue(service) is true || ServiceDisposed.GetValue(service) is true)
                    throw new InvalidOperationException($"Omen service {service.GetType().Name} has already entered its lifecycle.");
                started.Add(service);
            }

            void Initialize(OmenServiceBase service)
            {
                try
                {
                    Begin(service);
                    if (service is DataShareManager && prepareDataShare)
                        DataShareTags.SetValue(service, preparedDataShareTags
                            ?? throw new InvalidOperationException("Data share metadata preparation did not complete."));
                    else
                        InitializeService(service);
                    ServiceInitialized.SetValue(service, true);
                }
                catch (Exception exception)
                {
                    PluginLog.Error(exception,
                        "Omen service {ServiceName} failed to initialize.", service.GetType().Name);
                    throw;
                }
            }
        }, cancellationToken);
    }

    public ValueTask StopServicesAsync()
    {
        lock (lifecycleLock)
            return new(servicesStopping ??= StopCoreAsync());
    }

    private async Task StopCoreAsync()
    {
        if (serviceStartup is { } startup)
        {
            try
            {
                await startup.ConfigureAwait(false);
            }
            catch (Exception) when (!startup.IsCompletedSuccessfully)
            {
            }
        }
        List<Exception> errors = [];
        try
        {
            await StopTaskHelpersAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        try
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        if (errors.Count > 0)
            throw new AggregateException("Services failed to stop.", errors);
    }

    private async ValueTask StopStartedServicesAsync()
    {
        List<Exception> errors = [];
        var services = started.AsEnumerable().Reverse().ToArray();
        try
        {
            await ReleaseAsync(services, service =>
            {
                try
                {
                    UninitializeService(service);
                }
                finally
                {
                    ServiceInitialized.SetValue(service, false);
                    ServiceDisposed.SetValue(service, true);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            started.Clear();
        }
        if (errors.Count > 0)
            throw new AggregateException("Omen services failed to stop.", errors);
    }

    /// <summary>停止并等待宿主登记的所有任务队列，在释放其依赖之前调用。</summary>
    public Task StopTaskHelpersAsync()
    {
        lock (lifecycleLock)
            return helpersStopping ??= TaskHelper.StopAllAsync();
    }

    private static async ValueTask ReleaseAsync<T>
    (
        IReadOnlyList<T> resources,
        Action<T>        release
    )
    {
        List<Exception> errors = [];
        await FrameworkWorkScheduler.RunAsync(resources, resource =>
        {
            try
            {
                release(resource);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }).ConfigureAwait(false);
        if (errors.Count > 0)
            throw new AggregateException("Omen service resources failed to stop.", errors);
    }

    private static void Invoke
    (
        MethodInfo        method,
        object            target,
        params object?[]? arguments
    )
    {
        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    #region 常量

    private const BindingFlags MEMBER_FLAGS = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    #endregion
}
