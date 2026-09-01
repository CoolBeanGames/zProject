namespace PromptQueue.Core.Models;

/// <summary>A single checkable line inside a <see cref="TaskItem"/>.</summary>
public sealed class Subtask : Observable
{
    private string _text = "";
    private bool _done;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public bool Done
    {
        get => _done;
        set => Set(ref _done, value);
    }

    public Subtask Clone() => new() { Text = Text, Done = Done };
}
