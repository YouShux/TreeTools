using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Lumina.Excel;

namespace TreeTools.Data;

public static class SharedSheetMetadata
{
    /// <summary>在所属生命周期的查表任务全部停止后释放行类型元数据，保留共享表和客户端行类型。</summary>
    public static int Release(ExcelModule module, Assembly owner)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(owner);
        var context = AssemblyLoadContext.GetLoadContext(owner);
        if (context is not { IsCollectible: true })
            throw new InvalidOperationException("The sheet metadata owner must belong to a collectible load context.");
        var property = typeof(ExcelModule).GetProperty("SheetAttributeCache", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.PropertyType != typeof(ConcurrentDictionary<Type, SheetAttribute>) ||
            property.GetValue(module) is not ConcurrentDictionary<Type, SheetAttribute> cache)
            throw new MissingMemberException(typeof(ExcelModule).FullName, "SheetAttributeCache");

        var removed = 0;
        foreach (var type in cache.Keys.Where(type => AssemblyLoadContext.GetLoadContext(type.Assembly) == context).ToArray())
            if (cache.TryRemove(type, out _)) removed++;
        return removed;
    }
}
