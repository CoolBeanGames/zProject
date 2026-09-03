using System.Threading;

namespace PromptQueue.Core.Operator;

/// <summary>
/// A disposable wrapper around a system-wide <see cref="Mutex"/> so every
/// process that writes tasks.xml (the app, the operator exe, the web server)
/// takes its turn. Abandoned mutexes (a crash while held) are treated as
/// acquired, since the next holder re-reads and re-writes the file wholesale.
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    public CrossProcessLock(string name, int timeoutMs = 15000)
    {
        _mutex = new Mutex(false, name);
        try
        {
            _held = _mutex.WaitOne(timeoutMs);
        }
        catch (AbandonedMutexException)
        {
            _held = true;
        }
    }

    public void Dispose()
    {
        if (_held)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned — nothing to release */ }
        }
        _mutex.Dispose();
    }
}
