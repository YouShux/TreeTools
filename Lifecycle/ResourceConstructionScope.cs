using System.Linq;
using System.Threading.Tasks;

namespace TreeTools.Lifecycle;

public sealed class ResourceConstructionScope
{
    private readonly List<object> resources = [];

    private ResourceConstructionScope()
    {
    }

    public T Track<T>(T resource)
    {
        if (resource is IDisposable or IAsyncDisposable && !resources.Any(item => ReferenceEquals(item, resource)))
            resources.Add(resource);
        return resource;
    }

    public static async Task<T> CreateAsync<T>(Func<ResourceConstructionScope, Task<T>> factory, Func<Action, Task>? dispatcher = null)
    {
        var scope = new ResourceConstructionScope();
        try
        {
            return await factory(scope);
        }
        catch (Exception initializationError)
        {
            List<Exception> errors = [initializationError];
            foreach (var resource in scope.resources.AsEnumerable().Reverse())
            {
                try
                {
                    if (resource is IAsyncDisposable asynchronous)
                        await asynchronous.DisposeAsync();
                    else if (resource is IDisposable synchronous)
                    {
                        if (dispatcher is null)
                            synchronous.Dispose();
                        else
                            await dispatcher(synchronous.Dispose).ConfigureAwait(false);
                    }
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
            }
            if (errors.Count > 1)
                throw new AggregateException("Resource construction rollback failed.", errors);
            throw;
        }
    }
}
