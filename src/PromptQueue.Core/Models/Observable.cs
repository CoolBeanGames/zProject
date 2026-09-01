using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PromptQueue.Core.Models;

/// <summary>
/// Minimal INotifyPropertyChanged base used by the model layer so the WPF
/// front-end can bind directly to Core objects. Kept UI-framework agnostic so
/// the same models can back a future web server.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
