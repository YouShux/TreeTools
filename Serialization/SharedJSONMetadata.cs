using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace TreeTools.Serialization;

public static class SharedJSONMetadata
{
    public sealed record Result(int Matched, int Removed, IReadOnlyDictionary<string, int> Caches, IReadOnlyList<string> Failures);

    public static Result Inspect(Assembly owner) => Process(owner, false);

    /// <summary>仅在所属生命周期的所有元数据使用者停止后调用。</summary>
    public static Result Release(Assembly owner) => Process(owner, true);

    private static Result Process(Assembly owner, bool remove)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var context = AssemblyLoadContext.GetLoadContext(owner);
        if (context is not { IsCollectible: true })
            return new(0, 0, new Dictionary<string, int>(), ["The metadata owner must belong to a collectible load context."]);
        var assembly = typeof(JsonConvert).Assembly;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> failures = [];
        var matched = 0;
        var removed = 0;
        Check("DefaultContractResolver", () =>
        {
            var resolver = assembly.GetType("Newtonsoft.Json.Serialization.DefaultContractResolver", true)!;
            return Field(resolver, "_contractCache", Field(resolver, "_instance"));
        });
        foreach (var attribute in new[] { typeof(JsonContainerAttribute), typeof(JsonObjectAttribute), typeof(JsonArrayAttribute), typeof(JsonConverterAttribute),
                     typeof(DataContractAttribute), typeof(DataMemberAttribute) })
            Check(attribute.Name, () => Field(assembly.GetType("Newtonsoft.Json.Serialization.CachedAttributeGetter`1", true)!
                .MakeGenericType(attribute), "TypeAttributeCache"));
        Check("AssociatedMetadataTypes", () => Nested("Newtonsoft.Json.Serialization.JsonTypeReflector+AssociatedMetadataTypesCache"));
        Check("Creators", () => Nested("Newtonsoft.Json.Serialization.JsonTypeReflector+CreatorCache"));
        Check("EnumValues", () => Field(assembly.GetType("Newtonsoft.Json.Utilities.EnumUtils", true)!, "ValuesAndNamesPerEnum"));
        Check("CastConverters", () => Nested("Newtonsoft.Json.Utilities.ConvertUtils+CastConverters"));
        Check("KeyValuePairs", () => Field(assembly.GetType("Newtonsoft.Json.Converters.KeyValuePairConverter", true)!, "ReflectionObjectPerType"));
        CheckSystemTextJSON();
        CheckTypeDescriptors();
        return new(matched, removed, counts, failures);

        object Nested(string name) => Field(assembly.GetType(name, true)!, "Instance");

        void Check(string name, Func<object> getStore, bool unwrapStore = true, bool memberAccessorKeys = false)
        {
            try
            {
                var dictionary = getStore();
                Type[]? expectedArguments = null;
                if (unwrapStore)
                {
                    var storeType = dictionary.GetType();
                    if (!storeType.IsGenericType || storeType.GetGenericTypeDefinition().FullName != "Newtonsoft.Json.Utilities.ThreadSafeStore`2" ||
                        storeType.Assembly != assembly)
                        throw new InvalidOperationException("The JSON metadata store contract has changed.");
                    expectedArguments = storeType.GenericTypeArguments;
                    dictionary = Field(storeType, "_concurrentStore", dictionary);
                }
                var dictionaryType = dictionary.GetType();
                if (!dictionaryType.IsGenericType || dictionaryType.GetGenericTypeDefinition() != typeof(ConcurrentDictionary<,>) ||
                    (!unwrapStore && dictionaryType.GenericTypeArguments[0] !=
                        (memberAccessorKeys ? typeof(ValueTuple<string, Type, MemberInfo>) : typeof(Type))))
                    throw new InvalidOperationException("The JSON metadata store is not the expected concurrent dictionary.");
                var arguments = dictionaryType.GenericTypeArguments;
                if (expectedArguments is not null && !arguments.SequenceEqual(expectedArguments))
                    throw new InvalidOperationException("The JSON metadata dictionary arguments do not match its store.");
                var visit = typeof(SharedJSONMetadata).GetMethod(nameof(Visit), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(arguments);
                var result = ((int Matched, int Removed))visit.Invoke(null, [dictionary, context, remove])!;
                counts.Add(name, result.Matched);
                matched += result.Matched;
                removed += result.Removed;
            }
            catch (Exception exception)
            {
                var cause = exception.GetBaseException();
                failures.Add($"{name}: {cause.GetType().Name}: {cause.Message}");
            }
        }

        void CheckSystemTextJSON()
        {
            try
            {
                var options = System.Text.Json.JsonSerializerOptions.Default;
                var caching = options.GetType().GetField("_cachingContext", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(options.GetType().FullName, "_cachingContext");
                if (caching.GetValue(options) is { } optionsCache)
                    Check("SystemTextJSON.Default", () => Field(optionsCache.GetType(), "_cache", optionsCache), false);
                var resolver = typeof(System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver);
                var accessorField = resolver.GetField("s_memberAccessor", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(resolver.FullName, "s_memberAccessor");
                if (accessorField.GetValue(null) is { } accessor && accessor.GetType().FullName == "System.Text.Json.Serialization.Metadata.ReflectionEmitCachingMemberAccessor")
                {
                    var cache = Field(accessor.GetType(), "_cache", accessor);
                    Check("SystemTextJSON.MemberAccessors", () => Field(cache.GetType(), "_cache", cache), false, true);
                }
            }
            catch (Exception exception)
            {
                var cause = exception.GetBaseException();
                failures.Add($"SystemTextJSON: {cause.GetType().Name}: {cause.Message}");
            }
        }

        void CheckTypeDescriptors()
        {
            try
            {
                var descriptor = typeof(TypeDescriptor);
                var gate = Field(descriptor, "s_commonSyncObject");
                if (gate.GetType() != typeof(object))
                    throw new InvalidOperationException("The TypeDescriptor synchronization contract has changed.");
                // 上游在此门内写 provider 链和四张反射 Hashtable；不调用会创建新根的 GetProvider。
                lock (gate)
                {
                    var table = Field(descriptor, "s_providerTable");
                    if (table is not Hashtable providers || table.GetType().Assembly != descriptor.Assembly ||
                        table.GetType().FullName != "System.ComponentModel.WeakHashtable")
                        throw new InvalidOperationException("The TypeDescriptor provider table contract has changed.");
                    var nodeType = descriptor.GetNestedType("TypeDescriptionNode", BindingFlags.NonPublic)
                        ?? throw new TypeLoadException("The TypeDescriptor node contract is unavailable.");
                    var next = NodeField("Next", nodeType);
                    var provider = NodeField("Provider", typeof(TypeDescriptionProvider));
                    var reflected = descriptor.Assembly.GetType("System.ComponentModel.ReflectTypeDescriptionProvider", true)!;
                    HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
                    HashSet<object> instances = new(ReferenceEqualityComparer.Instance);
                    foreach (var root in providers.Values)
                        for (object? node = root; node is not null && visited.Add(node); node = next.GetValue(node))
                        {
                            if (node.GetType() != nodeType)
                                throw new InvalidOperationException("The TypeDescriptor provider node has an unexpected type.");
                            if (provider.GetValue(node) is { } instance && instance.GetType() == reflected)
                                instances.Add(instance);
                        }
                    Check("TypeDescriptor.DefaultProviders", () => Field(descriptor, "s_defaultProviderInitialized"), false);
                    Check("TypeDescriptor.ProviderTypes", () => Field(descriptor, "s_providerTypeTable"), false);
                    var index = 0;
                    foreach (var instance in instances)
                        Check($"TypeDescriptor.Types.{index++}", () => Field(reflected, "_typeData", instance), false);
                    CheckTable("TypeDescriptor.Providers", providers, true);
                    foreach (var name in new[] { "s_attributeCache", "s_propertyCache", "s_eventCache", "s_extendedPropertyCache" })
                    {
                        var field = reflected.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                        if (field is null || field.FieldType != typeof(Hashtable))
                            throw new MissingFieldException(reflected.FullName, name);
                        CheckTable($"TypeDescriptor.{name}", (Hashtable?)field.GetValue(null));
                    }

                    FieldInfo NodeField(string name, Type expected)
                    {
                        var field = nodeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                        return field is not null && field.FieldType == expected ? field : throw new MissingFieldException(nodeType.FullName, name);
                    }
                }
            }
            catch (Exception exception)
            {
                var cause = exception.GetBaseException();
                failures.Add($"TypeDescriptor: {cause.GetType().Name}: {cause.Message}");
            }
        }

        void CheckTable(string name, Hashtable? table, bool typeKeysOnly = false)
        {
            if (!typeKeysOnly && table is not null && table.GetType() != typeof(Hashtable))
                throw new InvalidOperationException($"The reflection cache {name} is not the expected Hashtable.");
            var keys = table?.Keys.Cast<object>().Where(key => typeKeysOnly ? key is Type type && Owns(type, context) : BelongsTo(key, context)).ToArray() ?? [];
            counts.Add(name, keys.Length);
            matched += keys.Length;
            if (!remove || table is null) return;
            foreach (var key in keys)
            {
                table.Remove(key);
                removed++;
            }
        }
    }

    private static (int Matched, int Removed) Visit<TKey, TValue>(object value, AssemblyLoadContext owner, bool remove) where TKey : notnull
    {
        var dictionary = (ConcurrentDictionary<TKey, TValue>)value;
        // 先验证完整键快照；未知键不会导致同一缓存内的部分删除。
        var keys = dictionary.Keys.Where(key => BelongsTo(key, owner)).ToArray();
        var removed = 0;
        if (remove)
            foreach (var key in keys)
                if (dictionary.TryRemove(key, out _)) removed++;
        return (keys.Length, removed);
    }

    private static bool BelongsTo(object key, AssemblyLoadContext owner)
    {
        if (key is ValueTuple<string, Type, MemberInfo> accessor)
            return Owns(accessor.Item2, owner) || accessor.Item3 is { } memberKey && BelongsTo(memberKey, owner);
        if (key is Type type) return Owns(type, owner);
        if (key is ParameterInfo parameter)
            return BelongsTo(parameter.Member, owner) || Owns(parameter.ParameterType, owner);
        if (key is MemberInfo member)
            return AssemblyLoadContext.GetLoadContext(member.Module.Assembly) == owner ||
                   (member.DeclaringType is { } declaring && Owns(declaring, owner)) ||
                   (member is MethodInfo { IsGenericMethod: true } method && method.GetGenericArguments().Any(argument => Owns(argument, owner)));
        var shape = key.GetType();
        if (shape.Assembly != typeof(JsonConvert).Assembly || !shape.IsGenericType ||
            shape.GetGenericTypeDefinition().FullName != "Newtonsoft.Json.Utilities.StructMultiKey`2" || !shape.IsValueType)
            throw new InvalidOperationException($"Unsupported JSON metadata key: {shape.FullName}.");
        var arguments = shape.GenericTypeArguments;
        if (arguments[0] != typeof(Type) || (arguments[1] != typeof(Type) && arguments[1] != typeof(Newtonsoft.Json.Serialization.NamingStrategy)))
            throw new InvalidOperationException($"Unsupported JSON metadata key arguments: {shape.FullName}.");
        var first = Component("Value1", arguments[0]);
        var second = Component("Value2", arguments[1]);
        return (first is Type firstType && Owns(firstType, owner)) ||
               (second is Type secondType && Owns(secondType, owner)) ||
               (second is not null && Owns(second.GetType(), owner));

        object? Component(string name, Type expected)
        {
            var field = shape.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field is not { IsInitOnly: true } || field.FieldType != expected)
                throw new MissingFieldException(shape.FullName, name);
            return field.GetValue(key);
        }
    }

    private static bool Owns(Type type, AssemblyLoadContext owner) =>
        AssemblyLoadContext.GetLoadContext(type.Assembly) == owner ||
        (type.HasElementType && Owns(type.GetElementType()!, owner)) ||
        (type.IsGenericType && type.GenericTypeArguments.Any(argument => Owns(argument, owner)));

    private static object Field(Type type, string name, object? instance = null)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        if (field is null || field.IsStatic != (instance is null))
            throw new MissingFieldException(type.FullName, name);
        return field.GetValue(instance) ?? throw new InvalidOperationException($"JSON metadata field {type.FullName}.{name} is null.");
    }
}
