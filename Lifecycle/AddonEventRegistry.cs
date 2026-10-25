using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;

namespace TreeTools.Lifecycle;

public sealed class AddonEventRegistry : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly List<(AddonEvent EventType, string AddonName, IAddonLifecycle.AddonEventDelegate Handler)> listeners = [];
    private bool disposed;

    public AddonEventRegistry(IAddonLifecycle addonLifecycle) => this.addonLifecycle = addonLifecycle;

    public void Register(
        AddonEvent eventType,
        string addonName,
        IAddonLifecycle.AddonEventDelegate handler)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        addonLifecycle.RegisterListener(eventType, addonName, handler);
        listeners.Add((eventType, addonName, handler));
    }

    public bool Release(
        AddonEvent eventType,
        string addonName,
        IAddonLifecycle.AddonEventDelegate handler)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var index = listeners.Count - 1; index >= 0; index--)
        {
            var listener = listeners[index];
            if (listener.EventType != eventType ||
                !string.Equals(listener.AddonName, addonName, StringComparison.Ordinal) ||
                !ReferenceEquals(listener.Handler, handler))
            {
                continue;
            }

            addonLifecycle.UnregisterListener(eventType, addonName, handler);
            listeners.RemoveAt(index);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        List<Exception>? errors = null;
        for (var index = listeners.Count - 1; index >= 0; index--)
        {
            var listener = listeners[index];
            try
            {
                addonLifecycle.UnregisterListener(listener.EventType, listener.AddonName, listener.Handler);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        listeners.Clear();
        if (errors is not null)
        {
            throw new AggregateException("One or more addon event listeners failed to unregister.", errors);
        }
    }
}
