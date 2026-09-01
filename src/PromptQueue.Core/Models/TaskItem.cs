namespace PromptQueue.Core.Models;

/// <summary>
/// A single unit of work in a project's prompt queue. Serialized to the
/// project's tasks.xml. The <see cref="Done"/> flag is the contract point for
/// external agents: an agent sets it to true and the app renders the row
/// struck through.
/// </summary>
public sealed class TaskItem : Observable
{
    private string _id = "";
    private string _prompt = "";
    private string _requirements = "";
    private bool _inProgress;
    private bool _done;
    private bool _error;
    private string _errorMessage = "";
    private bool _commit;
    private bool _build;
    private bool _release;
    private string _notes = "";
    private string _filesChanged = "";
    private int _order;

    /// <summary>Human-readable identifier, e.g. "MCA-3".</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    /// <summary>Prompt text passed to the agent that processes this task.</summary>
    public string Prompt
    {
        get => _prompt;
        set => Set(ref _prompt, value);
    }

    /// <summary>What the task must accomplish to count as complete.</summary>
    public string Requirements
    {
        get => _requirements;
        set => Set(ref _requirements, value);
    }

    /// <summary>Invisible tag: the task is currently being worked on.</summary>
    public bool InProgress
    {
        get => _inProgress;
        set => Set(ref _inProgress, value);
    }

    /// <summary>Invisible tag: the task has been completed.</summary>
    public bool Done
    {
        get => _done;
        set { if (Set(ref _done, value)) RaiseSection(); }
    }

    /// <summary>Set by an agent when the task could not be completed.</summary>
    public bool Error
    {
        get => _error;
        set { if (Set(ref _error, value)) RaiseSection(); }
    }

    /// <summary>
    /// Which list section the task belongs to. Rank 0 = an unfinished task an
    /// agent flagged with an error (shown first); 1 = active; 2 = completed
    /// (shown last).
    /// </summary>
    public int SectionRank => Error && !Done ? 0 : Done ? 2 : 1;

    /// <summary>Human-readable section name, used to group the task list.</summary>
    public string SectionKey => SectionRank switch
    {
        0 => "Needs attention",
        2 => "Completed",
        _ => "Active",
    };

    private void RaiseSection()
    {
        Raise(nameof(SectionRank));
        Raise(nameof(SectionKey));
    }

    /// <summary>Explanation posted by the agent when <see cref="Error"/> is set.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set => Set(ref _errorMessage, value);
    }

    /// <summary>When set, the project is committed and pushed once this task is done.</summary>
    public bool Commit
    {
        get => _commit;
        set => Set(ref _commit, value);
    }

    /// <summary>When set, the project is built to builds/debug once this task is done.</summary>
    public bool Build
    {
        get => _build;
        set => Set(ref _build, value);
    }

    /// <summary>When set, a release build (builds/release) is produced once this task is done.</summary>
    public bool Release
    {
        get => _release;
        set => Set(ref _release, value);
    }

    /// <summary>Free-form notes written by the agent.</summary>
    public string Notes
    {
        get => _notes;
        set => Set(ref _notes, value);
    }

    /// <summary>Files the agent changed while completing the task.</summary>
    public string FilesChanged
    {
        get => _filesChanged;
        set => Set(ref _filesChanged, value);
    }

    /// <summary>Zero-based position within the project's task list.</summary>
    public int Order
    {
        get => _order;
        set => Set(ref _order, value);
    }

    public TaskItem Clone() => new()
    {
        Id = Id,
        Prompt = Prompt,
        Requirements = Requirements,
        InProgress = InProgress,
        Done = Done,
        Error = Error,
        ErrorMessage = ErrorMessage,
        Commit = Commit,
        Build = Build,
        Release = Release,
        Notes = Notes,
        FilesChanged = FilesChanged,
        Order = Order,
    };

    public void CopyFrom(TaskItem other)
    {
        Id = other.Id;
        Prompt = other.Prompt;
        Requirements = other.Requirements;
        InProgress = other.InProgress;
        Done = other.Done;
        Error = other.Error;
        ErrorMessage = other.ErrorMessage;
        Commit = other.Commit;
        Build = other.Build;
        Release = other.Release;
        Notes = other.Notes;
        FilesChanged = other.FilesChanged;
        Order = other.Order;
    }
}
