using System.Collections.ObjectModel;
using PromptQueue.Core.Models;

namespace PromptQueue.App.ViewModels;

/// <summary>
/// Backs the inline "add task" / "edit task" sub-window. Holds an editable copy
/// of the task fields; committing copies them back onto the real
/// <see cref="TaskItem"/> via the supplied callback.
/// </summary>
public sealed class TaskFormViewModel : Observable
{
    private string _prompt;
    private string _requirements;
    private bool _inProgress;
    private bool _done;
    private bool _bug;
    private bool _error;
    private string _errorMessage;
    private bool _commit;
    private bool _build;
    private bool _release;
    private string _tagText;
    private string _notes;
    private string _filesChanged;
    private string _newSubtask = "";

    private readonly Action<TaskFormViewModel> _onSave;
    private readonly Action _onClose;

    public TaskFormViewModel(
        bool isNew,
        string id,
        TaskItem? source,
        Action<TaskFormViewModel> onSave,
        Action onClose)
    {
        IsNew = isNew;
        Id = id;
        _prompt = source?.Prompt ?? "";
        _requirements = source?.Requirements ?? "";
        _inProgress = source?.InProgress ?? false;
        _done = source?.Done ?? false;
        _bug = source?.Bug ?? false;
        _error = source?.Error ?? false;
        _errorMessage = source?.ErrorMessage ?? "";
        _commit = source?.Commit ?? false;
        _build = source?.Build ?? false;
        _release = source?.Release ?? false;
        _tagText = source?.TagText ?? "";
        _notes = source?.Notes ?? "";
        _filesChanged = source?.FilesChanged ?? "";
        _onSave = onSave;
        _onClose = onClose;

        if (source != null)
        {
            foreach (var s in source.Subtasks)
                Subtasks.Add(s.Clone());
        }
        Subtasks.CollectionChanged += (_, _) => Raise(nameof(HasSubtasks));

        AddSubtaskCommand = new RelayCommand(() =>
        {
            var text = NewSubtask.Trim();
            if (text.Length == 0)
                return;
            Subtasks.Add(new Subtask { Text = text });
            NewSubtask = "";
        });
        RemoveSubtaskCommand = new RelayCommand(p =>
        {
            if (p is Subtask s)
                Subtasks.Remove(s);
        });

        // Which agent-written sections to display: only those already populated
        // when the form opened (ZP-6). Latched so editing doesn't hide them.
        HasError = _error || !string.IsNullOrWhiteSpace(_errorMessage);
        HasNotes = !string.IsNullOrWhiteSpace(_notes);
        HasFilesChanged = !string.IsNullOrWhiteSpace(_filesChanged);

        SaveCommand = new RelayCommand(() =>
        {
            _onSave(this);
            _onClose();
        });
        CancelCommand = new RelayCommand(_onClose);
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "Add Task" : "Edit Task";

    public string Id { get; }

    public string Prompt
    {
        get => _prompt;
        set => Set(ref _prompt, value);
    }

    public string Requirements
    {
        get => _requirements;
        set => Set(ref _requirements, value);
    }

    public bool InProgress
    {
        get => _inProgress;
        set => Set(ref _inProgress, value);
    }

    public bool Done
    {
        get => _done;
        set => Set(ref _done, value);
    }

    public bool Bug
    {
        get => _bug;
        set => Set(ref _bug, value);
    }

    public bool Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => Set(ref _errorMessage, value);
    }

    public bool Commit
    {
        get => _commit;
        set => Set(ref _commit, value);
    }

    public bool Build
    {
        get => _build;
        set => Set(ref _build, value);
    }

    public bool Release
    {
        get => _release;
        set => Set(ref _release, value);
    }

    /// <summary>Comma-separated tags for this task.</summary>
    public string TagText
    {
        get => _tagText;
        set => Set(ref _tagText, value);
    }

    public ObservableCollection<Subtask> Subtasks { get; } = new();

    public bool HasSubtasks => Subtasks.Count > 0;

    public string NewSubtask
    {
        get => _newSubtask;
        set => Set(ref _newSubtask, value);
    }

    public RelayCommand AddSubtaskCommand { get; private set; } = null!;

    public RelayCommand RemoveSubtaskCommand { get; private set; } = null!;

    public string Notes
    {
        get => _notes;
        set => Set(ref _notes, value);
    }

    public string FilesChanged
    {
        get => _filesChanged;
        set => Set(ref _filesChanged, value);
    }

    /// <summary>Show the error section only when the task already carries an error.</summary>
    public bool HasError { get; }

    /// <summary>Show the notes section only when the task already carries notes.</summary>
    public bool HasNotes { get; }

    /// <summary>Show the files-changed section only when the task already carries it.</summary>
    public bool HasFilesChanged { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public void ApplyTo(TaskItem task)
    {
        task.Prompt = Prompt.Trim();
        task.Requirements = Requirements.Trim();
        task.InProgress = InProgress;
        task.Done = Done;
        task.Bug = Bug;
        task.Error = Error;
        task.ErrorMessage = ErrorMessage;
        task.Commit = Commit;
        task.Build = Build;
        task.Release = Release;
        task.TagText = TagText.Trim();
        task.Notes = Notes;
        task.FilesChanged = FilesChanged;

        task.Subtasks.Clear();
        foreach (var s in Subtasks)
            task.Subtasks.Add(new Subtask { Text = s.Text.Trim(), Done = s.Done });
    }
}
