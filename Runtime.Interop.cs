using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OmenTools;
using HookBackend = Dalamud.Plugin.Services.IGameInteropProvider.HookBackend;

namespace TreeTools;

internal class PluginInterfaceAdapter : DispatchProxy
{
    private static readonly MethodInfo FollowJump =
        typeof(IGameInteropProvider).Assembly.GetType("Dalamud.Hooking.Internal.HookManager", true)?
            .GetMethod("FollowJmp", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("Dalamud.Hooking.Internal.HookManager", "FollowJmp");
    private static readonly Type SafetyHookType =
        typeof(IGameInteropProvider).Assembly.GetType("Dalamud.Hooking.Internal.SafetyHookHook`1", true)
        ?? throw new TypeLoadException("Dalamud SafetyHook backend is unavailable.");
    private static readonly PropertyInfo HookProperty = typeof(DService).GetProperty(nameof(DService.Hook))
        ?? throw new MissingMemberException(typeof(DService).FullName, nameof(DService.Hook));
    private IDalamudPluginInterface source = null!;
    private InteropProvider interopProvider = null!;

    public static IDalamudPluginInterface Create(IDalamudPluginInterface source)
    {
        if (HookProperty.GetSetMethod(true) is null ||
            FollowJump.ReturnType != typeof(nint) || FollowJump.GetParameters() is not [{ ParameterType: var parameterType }] ||
            parameterType != typeof(nint) ||
            !SafetyHookType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Any(static constructor =>
                constructor.GetParameters() is [{ ParameterType: var addressType }, _, { ParameterType: var ownerType }] &&
                addressType == typeof(nint) && ownerType == typeof(Assembly)))
            throw new MissingMethodException("The fixed interop provider contract is unavailable.");
        var proxy = Create<IDalamudPluginInterface, PluginInterfaceAdapter>();
        var instance = (PluginInterfaceAdapter)proxy;
        instance.source = source;
        instance.interopProvider = new InteropProvider(
            source.GetService(typeof(IGameInteropProvider)) as IGameInteropProvider
            ?? throw new InvalidOperationException("Game interop service is unavailable."),
            source.GetService(typeof(ISigScanner)) as ISigScanner
            ?? throw new InvalidOperationException("Signature scanner service is unavailable."));
        return proxy;
    }

    public static void DisposeHooks(IDalamudPluginInterface pluginInterface)
    {
        if (pluginInterface is PluginInterfaceAdapter adapter)
            adapter.interopProvider.Dispose();
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (method.Name == nameof(IDalamudPluginInterface.GetService) && args is [Type serviceType] &&
            serviceType == typeof(IGameInteropProvider)) return interopProvider;
        try
        {
            var result = method.Invoke(source, args);
            if (method.Name == nameof(IDalamudPluginInterface.Inject) && result is true && args is [DService service, ..])
            {
                HookProperty.SetValue(service, interopProvider);
            }

            return result;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed unsafe class InteropProvider(IGameInteropProvider source, ISigScanner scanner) : IGameInteropProvider, IDisposable
    {
        private readonly ConditionalWeakTable<AssemblyLoadContext, HashSet<IDalamudHook>> hooks = new();
        private bool useSafetyHook;
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            List<Exception>? errors = null;
            foreach (var group in hooks)
            {
                foreach (var hook in group.Value)
                {
                    if (hook.IsDisposed)
                        continue;
                    try
                    {
                        hook.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (errors ??= []).Add(exception);
                    }
                }
                group.Value.Clear();
            }
            hooks.Clear();
            if (errors is not null)
                throw new AggregateException("Interop hooks failed to stop.", errors);
        }

        public void InitializeFromAttributes(object self) => source.InitializeFromAttributes(self);

        public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour) where T : Delegate =>
            source.HookFromFunctionPointerVariable(address, detour);

        public Hook<T> HookFromImport<T>(ProcessModule? module, string moduleName, string functionName, uint hintOrOrdinal, T detour)
            where T : Delegate => source.HookFromImport(module, moduleName, functionName, hintOrOrdinal, detour);

        public Hook<T> HookFromSymbol<T>(string moduleName, string exportName, T detour, HookBackend backend = HookBackend.Automatic)
            where T : Delegate => source.HookFromSymbol(moduleName, exportName, detour, backend);

        public Hook<T> HookFromAddress<T>(nint procAddress, T detour, HookBackend backend = HookBackend.Automatic) where T : Delegate
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (useSafetyHook && backend == HookBackend.Automatic)
                return CreateSafetyHook(procAddress, detour);

            try
            {
                return source.HookFromAddress(procAddress, detour, backend);
            }
            catch (Exception exception) when (backend == HookBackend.Automatic &&
                                               exception.Message.StartsWith("Unable to find memory location to fit MemoryBuffer", StringComparison.Ordinal))
            {
                useSafetyHook = true;
                return CreateSafetyHook(procAddress, detour);
            }
        }

        public Hook<T> HookFromAddress<T>(nuint procAddress, T detour, HookBackend backend = HookBackend.Automatic) where T : Delegate =>
            HookFromAddress((nint)procAddress, detour, backend);

        public Hook<T> HookFromAddress<T>(void* procAddress, T detour, HookBackend backend = HookBackend.Automatic) where T : Delegate =>
            HookFromAddress((nint)procAddress, detour, backend);

        public Hook<T> HookFromSignature<T>(string signature, T detour, HookBackend backend = HookBackend.Automatic) where T : Delegate =>
            HookFromAddress(scanner.ScanText(signature), detour, backend);

        private Hook<T> CreateSafetyHook<T>(nint procAddress, T detour) where T : Delegate
        {
            var safetyHook = SafetyHookType.MakeGenericType(typeof(T));
            var owner = detour.Method.Module.Assembly;
            var context = AssemblyLoadContext.GetLoadContext(owner)
                ?? throw new InvalidOperationException("The hook owner load context is unavailable.");
            try
            {
                var address = FollowJump.Invoke(null, [procAddress]);
                var hook = Activator.CreateInstance(safetyHook, BindingFlags.Instance | BindingFlags.NonPublic,
                                                null, [address, detour, owner], null) as Hook<T>
                       ?? throw new InvalidOperationException("SafetyHook initialization failed.");
                try
                {
                    hooks.GetValue(context, static _ => []).Add(hook);
                }
                catch
                {
                    hook.Dispose();
                    throw;
                }
                return hook;
            }
            catch (TargetInvocationException invocationException) when (invocationException.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
                throw;
            }
        }
    }
}
