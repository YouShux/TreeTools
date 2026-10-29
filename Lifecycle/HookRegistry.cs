using Dalamud.Hooking;
using OmenTools.Interop.Game.Models;

namespace TreeTools.Lifecycle;

public sealed class HookRegistry : IDisposable
{
    private readonly List<IDalamudHook> hooks    = [];
    private bool                        disposed;

    public Hook<T> Register<T>
    (
        CompSig signature,
        T       detour
    ) where T : Delegate
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var hook = signature.GetHook(detour);
        try
        {
            hook.Enable();
            hooks.Add(hook);
            return hook;
        }
        catch
        {
            hook.Dispose();
            throw;
        }
    }

    public void Release(IDalamudHook? hook)
    {
        if (hook is null || !hooks.Remove(hook))
        {
            return;
        }

        hook.Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        List<Exception>? errors = null;
        for (var index = hooks.Count - 1; index >= 0; index--)
        {
            try
            {
                hooks[index].Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        hooks.Clear();
        if (errors is not null)
        {
            throw new AggregateException("One or more hooks failed to dispose.", errors);
        }
    }
}
