using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace TreeTools.Reflection;

public static class SharedProxyMetadata
{
    /// <summary>所属上下文的全部代理使用者停止后，释放动态代理缓存引用。</summary>
    public static bool Release(Assembly owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var context = AssemblyLoadContext.GetLoadContext(owner);
        if (context is not { IsCollectible: true })
            throw new InvalidOperationException("The proxy metadata owner must belong to a collectible load context.");
        var generator = typeof(DispatchProxy).Assembly.GetType("System.Reflection.DispatchProxyGenerator", true)!;
        var cache = generator.GetField("s_alcProxyAssemblyMap", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new MissingFieldException(generator.FullName, "s_alcProxyAssemblyMap");
        var type = cache.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ConditionalWeakTable<,>) ||
            type.GenericTypeArguments[0] != typeof(AssemblyLoadContext) ||
            type.GenericTypeArguments[1] != generator.GetNestedType("ProxyAssembly", BindingFlags.NonPublic))
            throw new InvalidOperationException("The shared proxy metadata cache contract has changed.");
        // Remove 立即断开本项的依赖句柄，避免动态程序集与卸载中的 ALC 相互保留。
        var remove = type.GetMethod("Remove", [typeof(AssemblyLoadContext)])
            ?? throw new MissingMethodException(type.FullName, "Remove");
        return (bool)remove.Invoke(cache, [context])!;
    }
}
