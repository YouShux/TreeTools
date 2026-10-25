using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using OmenTools.OmenService.Abstractions;
using TreeTools.Lifecycle;
using TreeTools.Threading;

namespace TreeTools;

public sealed partial class Runtime
{
    private static readonly MethodInfo InitializeService = typeof(OmenServiceBase).GetMethod("Init", MEMBER_FLAGS)
        ?? throw new MissingMethodException(typeof(OmenServiceBase).FullName, "Init");
    private static readonly MethodInfo UninitializeService = typeof(OmenServiceBase).GetMethod("Uninit", MEMBER_FLAGS)
        ?? throw new MissingMethodException(typeof(OmenServiceBase).FullName, "Uninit");
    private static readonly PropertyInfo ServiceInitialized = typeof(OmenServiceBase).GetProperty("IsInitialized", MEMBER_FLAGS)
        ?? throw new MissingMemberException(typeof(OmenServiceBase).FullName, "IsInitialized");
    private static readonly PropertyInfo ServiceDisposed = typeof(OmenServiceBase).GetProperty("IsDisposed", MEMBER_FLAGS)
        ?? throw new MissingMemberException(typeof(OmenServiceBase).FullName, "IsDisposed");
    private readonly FeatureLifetime lifetime = new();
    private readonly List<OmenServiceBase> started = [];
    private Task? helpersStopping;
    private Task? servicesStopping;

    internal Task StartServicesAsync(IReadOnlyList<OmenServiceBase> services, CancellationToken cancellationToken)
    {
        lifetime.AddAsync(StopStartedServicesAsync);
        return FrameworkWorkScheduler.RunAsync(services, service =>
        {
            if (ServiceInitialized.GetValue(service) is true || ServiceDisposed.GetValue(service) is true)
                throw new InvalidOperationException($"Omen service {service.GetType().Name} has already entered its lifecycle.");
            started.Add(service);
            Invoke(InitializeService, service);
            ServiceInitialized.SetValue(service, true);
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
                    Invoke(UninitializeService, service);
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

    private static async ValueTask ReleaseAsync<T>(IReadOnlyList<T> resources, Action<T> release)
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

    private static void Invoke(MethodInfo method, object target, params object?[]? arguments)
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
