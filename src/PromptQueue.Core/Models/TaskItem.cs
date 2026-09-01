using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

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
    private string _name = "";
    private string _prompt = "";
    private string _requirements = "";
    private bool _inProgress;
    private bool _done;
    private bool _bug;
    private bool _error;
    private string _errorMessage = "";
    private bool _locked;
    private bool _archived;
    private string _blockedBy = "";
    private DateTime? _dateStarted;
    private DateTime? _dueDate;
    private bool _commit;
    private bool _build;
    private bool _release;
    private string _tagText = "";
    private string _notes = "";
    private string _filesChanged = "";
    private int _order;
    private bool _collapsed;

    /// <summary>Human-readable identifier, e.g. "MCA-3".</summary>
    public string Id
    {
        get => _id;
        set { if (Set(ref _id, value)) Raise(nameof(DisplayName)); }
    }

    /// <summary>Optional display name. When blank the task falls back to its id.</summary>
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { Raise(nameof(DisplayName)); Raise(nameof(HasName)); } }
    }

    /// <summary>The name shown on the card: <see cref="Name"/> if set, else <see cref="Id"/>.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>True when a distinct <see cref="Name"/> is set (so the id is worth showing too).</summary>
    public bool HasName => !string.IsNullOrWhiteSpace(Name);

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
        set { if (Set(ref _inProgress, value)) Raise(nameof(StatusText)); }
    }

    /// <summary>Invisible tag: the task has been completed.</summary>
    public bool Done
    {
        get => _done;
        set { if (Set(ref _done, value)) RaiseSection(); }
    }

    /// <summary>Marks the task as a bug. Bugs sort to the very top (list and file).</summary>
    public bool Bug
    {
        get => _bug;
        set { if (Set(ref _bug, value)) RaiseSection(); }
    }

    /// <summary>Set by an agent when the task could not be completed.</summary>
    public bool Error
    {
        get => _error;
        set { if (Set(ref _error, value)) RaiseSection(); }
    }

    /// <summary>
    /// When true an agent must ignore this task (it is "locked"). The card is
    /// greyed out. Serialized as &lt;locked&gt; so the contract is visible in the xml.
    /// </summary>
    public bool Locked
    {
        get => _locked;
        set { if (Set(ref _locked, value)) RaiseSection(); }
    }

    /// <summary>
    /// A completed task that has been archived. Still shown in the app (its own
    /// section) but an agent ignores it completely.
    /// </summary>
    public bool Archived
    {
        get => _archived;
        set { if (Set(ref _archived, value)) RaiseSection(); }
    }

    /// <summary>Id of a task that must be completed before this one (ZP-31).</summary>
    public string BlockedBy
    {
        get => _blockedBy;
        set { if (Set(ref _blockedBy, value)) { Raise(nameof(IsBlocked)); Raise(nameof(BlockedByLabel)); Raise(nameof(StatusText)); } }
    }

    public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockedBy);

    private string _blockedByName = "";

    /// <summary>Runtime-only: display name of the blocking task, resolved by the app.</summary>
    public string BlockedByName
    {
        get => _blockedByName;
        set { if (Set(ref _blockedByName, value)) Raise(nameof(BlockedByLabel)); }
    }

    /// <summary>e.g. "MCA-3" or "MCA-3 — Fix the parser".</summary>
    public string BlockedByLabel =>
        !IsBlocked ? "" :
        string.IsNullOrWhiteSpace(BlockedByName) ? BlockedBy : $"{BlockedBy} — {BlockedByName}";

    /// <summary>When the task was started (date + time).</summary>
    public DateTime? DateStarted
    {
        get => _dateStarted;
        set { if (Set(ref _dateStarted, value)) RaiseDates(); }
    }

    /// <summary>When the task is due (date + time).</summary>
    public DateTime? DueDate
    {
        get => _dueDate;
        set { if (Set(ref _dueDate, value)) RaiseDates(); }
    }

    private void RaiseDates()
    {
        Raise(nameof(DateStartedText));
        Raise(nameof(DueDateText));
        Raise(nameof(DatesSummary));
        Raise(nameof(HasDates));
    }

    public bool HasDates => DateStarted.HasValue || DueDate.HasValue;

    /// <summary>Runtime-only: whether the card is collapsed in the list (not serialized).</summary>
    public bool Collapsed
    {
        get => _collapsed;
        set { if (Set(ref _collapsed, value)) Raise(nameof(Expanded)); }
    }

    public bool Expanded => !Collapsed;

    private bool _tagsCollapsed;

    /// <summary>Runtime-only: whether the tag chips are hidden on an expanded card.</summary>
    public bool TagsCollapsed
    {
        get => _tagsCollapsed;
        set => Set(ref _tagsCollapsed, value);
    }

    /// <summary>Single-word status for the collapsed card, e.g. "Bug" / "Done".</summary>
    public string StatusText =>
        Archived ? "Archived" :
        Locked ? "Locked" :
        Done ? "Done" :
        Bug ? "Bug" :
        Error ? "Error" :
        IsBlocked ? "Blocked" :
        InProgress ? "In progress" :
        "Active";

    /// <summary>
    /// Which list section the task belongs to. Rank 0 = an unfinished bug
    /// (shown first); 1 = an unfinished task flagged with an error; 2 = active;
    /// 3 = completed; 4 = archived (shown last).
    /// </summary>
    public int SectionRank =>
        Archived ? 4 :
        !Done && Bug ? 0 :
        !Done && Error ? 1 :
        Done ? 3 : 2;

    /// <summary>Human-readable section name, used to group the task list.</summary>
    public string SectionKey => SectionRank switch
    {
        0 => "Bugs",
        1 => "Needs attention",
        3 => "Completed",
        4 => "Archived",
        _ => "Active",
    };

    private void RaiseSection()
    {
        Raise(nameof(SectionRank));
        Raise(nameof(SectionKey));
        Raise(nameof(StatusText));
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

    /// <summary>
    /// Comma-separated free-text tags for the task. Stored verbatim in the xml
    /// so an external agent cannot infer meaning from them.
    /// </summary>
    public string TagText
    {
        get => _tagText;
        set { if (Set(ref _tagText, value)) Raise(nameof(Tags)); }
    }

    /// <summary>The parsed, trimmed, de-duplicated tag list.</summary>
    public IReadOnlyList<string> Tags => SplitTags(TagText);

    public static IReadOnlyList<string> SplitTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();
        var seen = new List<string>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!seen.Contains(raw, StringComparer.OrdinalIgnoreCase))
                seen.Add(raw);
        }
        return seen;
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

    public TaskItem()
    {
        Subtasks.CollectionChanged += OnSubtasksChanged;
    }

    /// <summary>Optional checklist of smaller steps for this task.</summary>
    public ObservableCollection<Subtask> Subtasks { get; } = new();

    private void OnSubtasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (Subtask s in e.OldItems)
                s.PropertyChanged -= OnSubtaskPropertyChanged;
        if (e.NewItems != null)
            foreach (Subtask s in e.NewItems)
                s.PropertyChanged += OnSubtaskPropertyChanged;
        RaiseSubtaskProgress();
    }

    private void OnSubtaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Subtask.Done))
            RaiseSubtaskProgress();
    }

    private void RaiseSubtaskProgress()
    {
        Raise(nameof(HasSubtasks));
        Raise(nameof(SubtasksDone));
        Raise(nameof(SubtaskProgress));
    }

    public bool HasSubtasks => Subtasks.Count > 0;

    public int SubtasksDone => Subtasks.Count(s => s.Done);

    /// <summary>e.g. "2/5" — completed vs total subtasks.</summary>
    public string SubtaskProgress => Subtasks.Count == 0 ? "" : $"{SubtasksDone}/{Subtasks.Count}";

    private const string DateFormat = "yyyy-MM-dd HH:mm";

    public string DateStartedText => DateStarted?.ToString(DateFormat) ?? "";

    public string DueDateText => DueDate?.ToString(DateFormat) ?? "";

    /// <summary>e.g. "Due 2026-09-05 17:00" / "Started 2026-09-01 09:30".</summary>
    public string DatesSummary
    {
        get
        {
            var parts = new List<string>();
            if (DateStarted.HasValue) parts.Add($"Started {DateStartedText}");
            if (DueDate.HasValue) parts.Add($"Due {DueDateText}");
            return string.Join("  •  ", parts);
        }
    }

    public TaskItem Clone()
    {
        var copy = new TaskItem
        {
            Id = Id,
            Name = Name,
            Prompt = Prompt,
            Requirements = Requirements,
            InProgress = InProgress,
            Done = Done,
            Bug = Bug,
            Error = Error,
            ErrorMessage = ErrorMessage,
            Locked = Locked,
            Archived = Archived,
            BlockedBy = BlockedBy,
            DateStarted = DateStarted,
            DueDate = DueDate,
            Commit = Commit,
            Build = Build,
            Release = Release,
            TagText = TagText,
            Notes = Notes,
            FilesChanged = FilesChanged,
            Order = Order,
        };
        foreach (var s in Subtasks)
            copy.Subtasks.Add(s.Clone());
        return copy;
    }

    public void CopyFrom(TaskItem other)
    {
        Id = other.Id;
        Name = other.Name;
        Prompt = other.Prompt;
        Requirements = other.Requirements;
        InProgress = other.InProgress;
        Done = other.Done;
        Bug = other.Bug;
        Error = other.Error;
        ErrorMessage = other.ErrorMessage;
        Locked = other.Locked;
        Archived = other.Archived;
        BlockedBy = other.BlockedBy;
        DateStarted = other.DateStarted;
        DueDate = other.DueDate;
        Commit = other.Commit;
        Build = other.Build;
        Release = other.Release;
        TagText = other.TagText;
        Notes = other.Notes;
        FilesChanged = other.FilesChanged;
        Order = other.Order;
        Subtasks.Clear();
        foreach (var s in other.Subtasks)
            Subtasks.Add(s.Clone());
    }
}
