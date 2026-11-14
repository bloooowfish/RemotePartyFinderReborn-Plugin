using System;
using System.Collections.Generic;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class InteropHookBundle : IDisposable {
    private readonly List<IDisposable> _hooks = [];
    private bool _disposed;

    internal void Add(IDisposable hook) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(hook);

        _hooks.Add(hook);
    }

    internal void AddAndEnable<T>(T hook, Action<T> enable) where T : IDisposable {
        ArgumentNullException.ThrowIfNull(enable);

        Add(hook);
        try {
            enable(hook);
        } catch {
            Dispose();
            throw;
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        for (var index = _hooks.Count - 1; index >= 0; index--) {
            try {
                _hooks[index].Dispose();
            } catch (Exception ex) {
                try {
                    Plugin.Log?.Error(ex, "InteropHookBundle: failed to dispose hook.");
                } catch {
                    // Ignore when Plugin.Log is uninitialized in unit tests.
                }
            }
        }

        _hooks.Clear();
    }
}
