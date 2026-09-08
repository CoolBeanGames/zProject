using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using PromptQueue.Core.Models;

namespace PromptQueue.App.ViewModels;

/// <summary>
/// Backs the inline "add task" / "edit task" sub-window. Holds an editable copy
/// of the task fields; committing copies them back onto the real
/// <see cref="TaskItem"/> via the supplied callback.
/// </summary>
public sealed class TaskFormViewModel : Observable
{
    private string _name;
    private bool _isNote;
    private string _note;
    private bool _clearAfterReading;
    private string _prompt;
    private string _requirements;
    private bool _inProgress;
    private bool _done;
    private bool _priority;
    private bool _bug;
    private bool _error;
    private string _errorMessage;
    private bool _locked;
    private bool _archived;
    private string _blockedBy;
    private DateTime? _startDatePart;
    private string _startTimePart;
    private DateTime? _dueDatePart;
    private string _dueTimePart;
    private bool _commit;
    private bool _build;
    private bool _release;
    private bool _merge;
    private string _branch;
    private string _tagText;
    private string _notes;
    private string _filesChanged;
    private string _image;
    private string _newSubtask = "";

    private readonly Action<TaskFormViewModel> _onSave;
    private readonly Action _onClose;
    private readonly string? _projectDirectory;

    /// <summary>Sub-folder of the project directory that holds task images (ZP-59).</summary>
    public const string ImageFolderName = "task_images";

    public TaskFormViewModel(
        bool isNew,
        string id,
        TaskItem? source,
        Action<TaskFormViewModel> onSave,
        Action onClose,
        IEnumerable<TaskItem>? peers = null,
        string? projectDirectory = null)
    {
        IsNew = isNew;
        Id = id;
        _projectDirectory = projectDirectory;
        _name = source?.Name ?? "";
        _isNote = source?.IsNote ?? false;
        _note = source?.Note ?? "";
        _clearAfterReading = source?.ClearAfterReading ?? false;
        _prompt = source?.Prompt ?? "";
        _requirements = source?.Requirements ?? "";
        _inProgress = source?.InProgress ?? false;
        _done = source?.Done ?? false;
        _priority = source?.Priority ?? false;
        _bug = source?.Bug ?? false;
        _error = source?.Error ?? false;
        _errorMessage = source?.ErrorMessage ?? "";
        _locked = source?.Locked ?? false;
        _archived = source?.Archived ?? false;
        _blockedBy = source?.BlockedBy ?? "";
        _startDatePart = source?.DateStarted?.Date;
        _startTimePart = source?.DateStarted?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _dueDatePart = source?.DueDate?.Date;
        _dueTimePart = source?.DueDate?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _commit = source?.Commit ?? false;
        _build = source?.Build ?? false;
        _release = source?.Release ?? false;
        _merge = source?.Merge ?? false;
        _branch = string.IsNullOrWhiteSpace(source?.Branch) ? "main" : source.Branch;
        _tagText = source?.TagText ?? "";
        _notes = source?.Notes ?? "";
        _filesChanged = source?.FilesChanged ?? "";
        _image = source?.Image ?? "";
        foreach (var attachment in source?.Attachments ?? Enumerable.Empty<string>())
            if (!Attachments.Contains(attachment, StringComparer.OrdinalIgnoreCase))
                Attachments.Add(attachment);
        if (_image.Length > 0 && !Attachments.Contains(_image, StringComparer.OrdinalIgnoreCase))
            Attachments.Insert(0, _image);
        _onSave = onSave;
        _onClose = onClose;

        KnownBranches = (peers ?? Enumerable.Empty<TaskItem>())
            .Select(t => t.Branch)
            .Concat(new[] { "main", _branch })
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => b.Equals("main", StringComparison.OrdinalIgnoreCase) ? "" : b, StringComparer.OrdinalIgnoreCase)
            .ToList();

        KnownTags = (peers ?? Enumerable.Empty<TaskItem>())
            .SelectMany(t => t.Tags)
            .Concat(TaskItem.SplitTags(_tagText))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        BlockCandidates = (peers ?? Enumerable.Empty<TaskItem>())
            .Where(t => !string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
            // ZP-54: only offer live, named tasks as blockers - hide done,
            // archived, and unnamed tasks.
            .Where(t => !t.Done && !t.Archived && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => $"{t.Id} — {t.Name}")
            .ToList();

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

        ChooseImageCommand = new RelayCommand(ChooseAttachments, () => !string.IsNullOrEmpty(_projectDirectory));
        RemoveImageCommand = new RelayCommand(p => RemoveAttachment(p as string), p => p is string name && Attachments.Contains(name));

        SaveCommand = new RelayCommand(() =>
        {
            _onSave(this);
            _onClose();
        });
        CancelCommand = new RelayCommand(_onClose);
    }

    /// <summary>
    /// Copies a picked image into the project's <c>task_images</c> folder and
    /// records its file name on the task (ZP-59).
    /// </summary>
    private void ChooseAttachments()
    {
        if (string.IsNullOrEmpty(_projectDirectory))
            return;

        var dlg = new OpenFileDialog
        {
            Title = "Attach files to this task",
            Filter = "Supported files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.zscript;*.zsheet;*.csv;*.txt;*.docx;*.pdf|Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|zScript files|*.zscript|zSheet files|*.zsheet|Documents and data|*.csv;*.txt;*.docx;*.pdf",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var folder = System.IO.Path.Combine(_projectDirectory, ImageFolderName);
            System.IO.Directory.CreateDirectory(folder);

            foreach (var sourcePath in dlg.FileNames)
            {
                var ext = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext))
                    continue;
                var safeBase = string.Concat(System.IO.Path.GetFileNameWithoutExtension(sourcePath)
                    .Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch == ',' ? '_' : ch));
                if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "attachment";
                var stem = $"{Id}-{safeBase}";
                var fileName = stem + ext;
                var suffix = 2;
                while (System.IO.File.Exists(System.IO.Path.Combine(folder, fileName)) ||
                       Attachments.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    fileName = $"{stem}-{suffix++}{ext}";
                System.IO.File.Copy(sourcePath, System.IO.Path.Combine(folder, fileName));
                Attachments.Add(fileName);
            }
            RefreshPrimaryImage();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not attach the image:\n\n{ex.Message}",
                "zProject", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "Add Task" : "Edit Task";

    public string Id { get; }

    /// <summary>"ID — name" strings for the Blocked-by autocomplete.</summary>
    public IReadOnlyList<string> BlockCandidates { get; }

    /// <summary>Every tag already used in the project, for the Tags ghost-autocomplete (ZP-66).</summary>
    public IReadOnlyList<string> KnownTags { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".zscript", ".zsheet", ".csv", ".txt", ".docx", ".pdf",
    };

    public ObservableCollection<string> Attachments { get; } = new();

    /// <summary>
    /// Stores an image supplied by the browser clipboard in the same collision-safe
    /// attachment folder used by the file picker. The editor has not necessarily
    /// been saved yet, so this updates the form copy and lets the normal Save flow
    /// persist the attachment name on the task.
    /// </summary>
    public string AddPastedImage(byte[] bytes, string? contentType, string? suggestedName)
    {
        if (string.IsNullOrEmpty(_projectDirectory))
            throw new InvalidOperationException("This task does not have a project attachment folder.");
        if (bytes.Length == 0)
            throw new InvalidDataException("The clipboard image was empty.");
        if (bytes.Length > 25 * 1024 * 1024)
            throw new InvalidDataException("Clipboard images must be 25 MB or smaller.");

        var extension = contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            _ => System.IO.Path.GetExtension(suggestedName ?? "").ToLowerInvariant(),
        };
        if (!IsImage("image" + extension))
            throw new InvalidDataException("The clipboard did not contain a supported image type.");

        var folder = System.IO.Path.Combine(_projectDirectory, ImageFolderName);
        System.IO.Directory.CreateDirectory(folder);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(suggestedName ?? "clipboard-image");
        var safeBase = string.Concat(baseName.Select(ch =>
            System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch == ',' ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "clipboard-image";

        var stem = $"{Id}-{safeBase}";
        var fileName = stem + extension;
        var suffix = 2;
        while (System.IO.File.Exists(System.IO.Path.Combine(folder, fileName)) ||
               Attachments.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            fileName = $"{stem}-{suffix++}{extension}";

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, fileName), bytes);
        Attachments.Add(fileName);
        RefreshPrimaryImage();
        return fileName;
    }

    public void RemoveAttachment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = Attachments.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Attachments.Remove(existing);
        RefreshPrimaryImage();
    }

    private void RefreshPrimaryImage() => Image = Attachments.FirstOrDefault(IsImage) ?? "";

    public static bool IsImage(string name) =>
        System.IO.Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    public bool IsNote
    {
        get => _isNote;
        set => Set(ref _isNote, value);
    }

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public bool ClearAfterReading
    {
        get => _clearAfterReading;
        set => Set(ref _clearAfterReading, value);
    }

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

    public bool Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    public bool Bug
    {
        get => _bug;
        set => Set(ref _bug, value);
    }

    public bool Locked
    {
        get => _locked;
        set => Set(ref _locked, value);
    }

    public bool Archived
    {
        get => _archived;
        set => Set(ref _archived, value);
    }

    /// <summary>Raw "ID" or "ID — name" text of the blocking task.</summary>
    public string BlockedBy
    {
        get => _blockedBy;
        set => Set(ref _blockedBy, value);
    }

    public DateTime? StartDatePart
    {
        get => _startDatePart;
        set => Set(ref _startDatePart, value);
    }

    public string StartTimePart
    {
        get => _startTimePart;
        set => Set(ref _startTimePart, value);
    }

    public DateTime? DueDatePart
    {
        get => _dueDatePart;
        set => Set(ref _dueDatePart, value);
    }

    public string DueTimePart
    {
        get => _dueTimePart;
        set => Set(ref _dueTimePart, value);
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

    public bool Merge
    {
        get => _merge;
        set => Set(ref _merge, value);
    }

    /// <summary>The project branch for this task (default 'main').</summary>
    public string Branch
    {
        get => _branch;
        set => Set(ref _branch, value);
    }

    public IReadOnlyList<string> KnownBranches { get; }

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

    /// <summary>File name of the attached task image (ZP-59), or "" when none.</summary>
    public string Image
    {
        get => _image;
        set
        {
            if (Set(ref _image, value))
            {
                Raise(nameof(HasImage));
                Raise(nameof(ImagePreviewPath));
                RemoveImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasImage => !string.IsNullOrWhiteSpace(_image);

    /// <summary>Absolute path to the attached image for the preview, or "" when none.</summary>
    public string ImagePreviewPath =>
        HasImage && !string.IsNullOrEmpty(_projectDirectory)
            ? System.IO.Path.Combine(_projectDirectory, ImageFolderName, _image)
            : "";

    public RelayCommand ChooseImageCommand { get; private set; } = null!;

    public RelayCommand RemoveImageCommand { get; private set; } = null!;

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
        task.Name = Name.Trim();
        task.IsNote = IsNote;
        task.Note = IsNote ? Note.Trim() : "";
        task.ClearAfterReading = IsNote && ClearAfterReading;
        task.Prompt = Prompt.Trim();
        task.Requirements = Requirements.Trim();
        task.InProgress = InProgress;
        task.Done = Done;
        task.Priority = Priority;
        task.Bug = Bug;
        task.Error = Error;
        task.ErrorMessage = ErrorMessage;
        task.Locked = Locked;
        task.Archived = Archived;
        task.BlockedBy = ExtractId(BlockedBy);
        task.DateStarted = Combine(StartDatePart, StartTimePart);
        task.DueDate = Combine(DueDatePart, DueTimePart);
        task.Commit = Commit;
        task.Build = Build;
        task.Release = Release;
        task.Merge = Merge;
        task.Branch = string.IsNullOrWhiteSpace(Branch) ? "main" : Branch.Trim();
        task.TagText = TagText.Trim();
        task.Notes = Notes;
        task.FilesChanged = FilesChanged;
        task.Attachments.Clear();
        foreach (var attachment in Attachments)
            task.Attachments.Add(attachment);
        task.Image = task.Attachments.FirstOrDefault(IsImage) ?? "";

        if (IsNote)
        {
            task.Prompt = "";
            task.Requirements = "";
            task.InProgress = false;
            task.Priority = false;
            task.Bug = false;
            task.Error = false;
            task.ErrorMessage = "";
            task.LockKey = "";
            task.Locked = false;
            task.BlockedBy = "";
            task.DateStarted = null;
            task.DueDate = null;
            task.Commit = false;
            task.Build = false;
            task.Release = false;
            task.Merge = false;
            task.TagText = "";
            task.Notes = "";
            task.FilesChanged = "";
            task.Image = "";
            task.Attachments.Clear();
        }

        task.Subtasks.Clear();
        if (IsNote)
            return;
        foreach (var s in Subtasks.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            task.Subtasks.Add(new Subtask { Text = s.Text.Trim(), Done = s.Done });
    }

    /// <summary>Takes the leading id token from "ID" or "ID — name".</summary>
    private static string ExtractId(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return "";
        var dash = text.IndexOf('—');
        if (dash < 0)
            dash = text.IndexOf(" - ", StringComparison.Ordinal);
        return (dash > 0 ? text[..dash] : text).Trim();
    }

    private static DateTime? Combine(DateTime? date, string time)
    {
        if (date is null)
            return null;
        var d = date.Value.Date;
        if (!string.IsNullOrWhiteSpace(time) &&
            TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var t))
            return d + t;
        return d;
    }
}
