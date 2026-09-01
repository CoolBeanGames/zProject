using PromptQueue.Core.Models;

namespace PromptQueue.App.ViewModels;

/// <summary>
/// Backs the inline text-editor overlay used for every free-form text target:
/// the global design / instructions / prompt and their per-project overrides.
/// </summary>
public sealed class TextEditorViewModel : Observable
{
    private string _text;
    private readonly Action<string> _onSave;
    private readonly Action _onClose;

    public TextEditorViewModel(
        string title,
        string subtitle,
        string text,
        Action<string> onSave,
        Action onClose)
    {
        Title = title;
        Subtitle = subtitle;
        _text = text ?? "";
        _onSave = onSave;
        _onClose = onClose;

        SaveCommand = new RelayCommand(() =>
        {
            _onSave(Text);
            _onClose();
        });
        CancelCommand = new RelayCommand(_onClose);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }
}
