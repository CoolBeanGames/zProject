using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using PromptQueue.Core.Models;
using PromptQueue.Core.Storage;

namespace PromptQueue.App.ViewModels;

/// <summary>
/// Root view-model. Owns the workspace, the current project selection and the
/// single inline overlay (text editor or task form) shown over the main panel.
/// </summary>
public sealed class MainViewModel : Observable
{
    private Project? _selectedProject;
    private object? _overlay;
    private string _statusText = "Ready";
    private ICollectionView? _tasksView;
    private bool _completedCollapsed;
    private string _newTaskTagText = "";

    public MainViewModel(Workspace workspace)
    {
        Workspace = workspace;

        NewProjectCommand = new RelayCommand(NewProject);
        SaveAllCommand = new RelayCommand(SaveAll);
        SaveCurrentCommand = new RelayCommand(SaveCurrentProject, () => SelectedProject != null);
        ReloadProjectCommand = new RelayCommand(ReloadProject, () => SelectedProject != null);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

        EditGlobalDesignCommand = new RelayCommand(() => EditText(
            "Global Design", "Default design requirements for every project",
            Workspace.GlobalDesign, v => { Workspace.GlobalDesign = v; Workspace.Save(); }));
        EditGlobalInstructionsCommand = new RelayCommand(() => EditText(
            "Global Instructions", "Default agent instructions for every project",
            Workspace.GlobalInstructions, v => { Workspace.GlobalInstructions = v; Workspace.Save(); }));
        EditGlobalPromptCommand = new RelayCommand(() => EditText(
            "Global Prompt", "Base prompt that tells an agent how to use tasks and designs",
            Workspace.GlobalPrompt, v => { Workspace.GlobalPrompt = v; Workspace.Save(); }));

        EditLocalDesignCommand = new RelayCommand(() => EditProjectText(
            "Design", "Design requirements for this project",
            p => p.LocalDesign, (p, v) => p.LocalDesign = v), HasProject);
        EditLocalInstructionsCommand = new RelayCommand(() => EditProjectText(
            "Instructions", "Agent instructions for this project",
            p => p.LocalInstructions, (p, v) => p.LocalInstructions = v), HasProject);
        EditLocalPromptCommand = new RelayCommand(() => EditProjectText(
            "Prompt", "Base prompt for this project",
            p => p.LocalPrompt, (p, v) => p.LocalPrompt = v), HasProject);

        AddTaskCommand = new RelayCommand(AddTask, HasProject);
        EditTaskCommand = new RelayCommand(p => EditTask(p as TaskItem));
        DeleteTaskCommand = new RelayCommand(p => DeleteTask(p as TaskItem));
        ToggleTaskDoneCommand = new RelayCommand(p => ToggleDone(p as TaskItem));
        ToggleTaskLockCommand = new RelayCommand(p => ToggleLock(p as TaskItem));
        ToggleTaskCollapsedCommand = new RelayCommand(p => ToggleCollapsed(p as TaskItem));
        ArchiveDoneCommand = new RelayCommand(ArchiveDone, () => SelectedProject?.Tasks.Any(t => t.Done && !t.Archived) == true);
        ToggleCompletedCollapsedCommand = new RelayCommand(() => CompletedCollapsed = !CompletedCollapsed);

        if (Workspace.Projects.Count > 0)
            SelectedProject = Workspace.Projects[0];
    }

    public Workspace Workspace { get; }

    /// <summary>Raised when the user really wants to quit (File &gt; Exit).</summary>
    public event Action? ExitRequested;

    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (Set(ref _selectedProject, value))
            {
                CloseOverlay();
                RebuildTasksView();
                Raise(nameof(HasSelectedProject));
                RefreshCommandStates();
            }
        }
    }

    public bool HasSelectedProject => SelectedProject != null;

    /// <summary>
    /// The task list as shown: grouped into the "Needs attention" / "Active" /
    /// "Completed" sections. The underlying <see cref="Project.Tasks"/> is kept
    /// physically ordered to match, so drag-reorder stays index-based.
    /// </summary>
    public ICollectionView? TasksView => _tasksView;

    /// <summary>Whether the Completed section is collapsed (hidden) in the list.</summary>
    public bool CompletedCollapsed
    {
        get => _completedCollapsed;
        set => Set(ref _completedCollapsed, value);
    }

    public RelayCommand ToggleCompletedCollapsedCommand { get; }

    /// <summary>Every distinct tag used anywhere in the selected project (for autocomplete).</summary>
    public ObservableCollection<string> KnownTags { get; } = new();

    /// <summary>The header "tags" field — applied to newly created tasks.</summary>
    public string NewTaskTagText
    {
        get => _newTaskTagText;
        set => Set(ref _newTaskTagText, value);
    }

    private void RefreshKnownTags()
    {
        var tags = SelectedProject?.Tasks
            .SelectMany(t => t.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        KnownTags.Clear();
        foreach (var t in tags)
            KnownTags.Add(t);
    }

    public object? Overlay
    {
        get => _overlay;
        private set
        {
            if (Set(ref _overlay, value))
                Raise(nameof(HasOverlay));
        }
    }

    public bool HasOverlay => Overlay != null;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ---- Commands -------------------------------------------------------

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand SaveCurrentCommand { get; }
    public RelayCommand ReloadProjectCommand { get; }
    public RelayCommand ExitCommand { get; }

    public RelayCommand EditGlobalDesignCommand { get; }
    public RelayCommand EditGlobalInstructionsCommand { get; }
    public RelayCommand EditGlobalPromptCommand { get; }

    public RelayCommand EditLocalDesignCommand { get; }
    public RelayCommand EditLocalInstructionsCommand { get; }
    public RelayCommand EditLocalPromptCommand { get; }

    public RelayCommand AddTaskCommand { get; }
    public RelayCommand EditTaskCommand { get; }
    public RelayCommand DeleteTaskCommand { get; }
    public RelayCommand ToggleTaskDoneCommand { get; }
    public RelayCommand ToggleTaskLockCommand { get; }
    public RelayCommand ToggleTaskCollapsedCommand { get; }
    public RelayCommand ArchiveDoneCommand { get; }

    // ---- Projects ------------------------------------------------------

    private bool HasProject() => SelectedProject != null;

    private void NewProject()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a project directory",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
            return;

        var dir = dialog.FolderName;
        var existing = Workspace.Projects.FirstOrDefault(p =>
            string.Equals(p.Directory, Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

        var project = existing ?? Workspace.AddProject(dir);
        SelectedProject = project;
        StatusText = existing != null
            ? $"Opened existing project \"{project.Name}\""
            : $"Created project \"{project.Name}\" at {project.Directory}";
    }

    private void ReloadProject()
    {
        if (SelectedProject == null)
            return;
        ProjectStore.ReloadInto(SelectedProject);
        RebuildTasksView();
        StatusText = $"Reloaded \"{SelectedProject.Name}\" from disk";
    }

    /// <summary>Re-reads every known project from disk (used on window restore).</summary>
    public void ReloadAllProjects()
    {
        foreach (var project in Workspace.Projects)
            ProjectStore.ReloadInto(project);
        RebuildTasksView();
        StatusText = $"Reloaded {Workspace.Projects.Count} project(s) from disk";
    }

    private void SaveAll()
    {
        Workspace.Save();
        foreach (var project in Workspace.Projects)
            ProjectStore.Save(project);
        StatusText = $"Saved workspace and {Workspace.Projects.Count} project(s)";
    }

    private void SaveCurrentProject()
    {
        if (SelectedProject == null)
            return;
        ProjectStore.Save(SelectedProject);
        Workspace.SaveDataCfg();
        StatusText = $"Saved project \"{SelectedProject.Name}\"";
    }

    private void SaveCurrent()
    {
        if (SelectedProject != null)
            ProjectStore.Save(SelectedProject);
    }

    // ---- Text editing -------------------------------------------------

    private void EditText(string title, string subtitle, string current, Action<string> commit)
    {
        Overlay = new TextEditorViewModel(title, subtitle, current,
            saved =>
            {
                commit(saved);
                StatusText = $"Saved {title}";
            },
            CloseOverlay);
    }

    private void EditProjectText(
        string title, string subtitle,
        Func<Project, string> get, Action<Project, string> set)
    {
        var project = SelectedProject;
        if (project == null)
            return;
        Overlay = new TextEditorViewModel(
            $"{project.Name} — {title}", subtitle, get(project),
            saved =>
            {
                set(project, saved);
                ProjectStore.Save(project);
                StatusText = $"Saved {title} for \"{project.Name}\"";
            },
            CloseOverlay);
    }

    // ---- Task list view / sections ---------------------------------

    private void RebuildTasksView()
    {
        if (SelectedProject == null)
        {
            _tasksView = null;
            Raise(nameof(TasksView));
            return;
        }

        SortIntoSections(SelectedProject);

        ResolveBlockedByNames();

        var view = new ListCollectionView(SelectedProject.Tasks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskItem.SectionKey)));
        _tasksView = view;
        RefreshKnownTags();
        Raise(nameof(TasksView));
    }

    /// <summary>
    /// Stable-orders the project's tasks by section rank: error-and-unfinished
    /// first, then active, then completed. Order within a section is preserved.
    /// </summary>
    public static void SortIntoSections(Project project)
    {
        var sorted = project.Tasks.OrderBy(t => t.SectionRank).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var current = project.Tasks.IndexOf(sorted[i]);
            if (current != i)
                project.Tasks.Move(current, i);
        }
        ProjectStore.Normalize(project);
    }

    /// <summary>Re-sections, refreshes the grouped view and persists.</summary>
    private void AfterTasksChanged()
    {
        if (SelectedProject == null)
            return;
        SortIntoSections(SelectedProject);
        ResolveBlockedByNames();
        _tasksView?.Refresh();
        RefreshKnownTags();
        ArchiveDoneCommand.RaiseCanExecuteChanged();
        SaveCurrent();
        Workspace.Save();
    }

    private void ResolveBlockedByNames()
    {
        var project = SelectedProject;
        if (project == null)
            return;
        foreach (var t in project.Tasks)
        {
            if (!t.IsBlocked)
            {
                t.BlockedByName = "";
                continue;
            }
            var blocker = project.Tasks.FirstOrDefault(x =>
                string.Equals(x.Id, t.BlockedBy, StringComparison.OrdinalIgnoreCase));
            t.BlockedByName = blocker != null && !string.IsNullOrWhiteSpace(blocker.Name)
                ? blocker.Name : "";
        }
    }

    // ---- Tasks -------------------------------------------------------

    private void AddTask()
    {
        var project = SelectedProject;
        if (project == null)
            return;

        var newId = project.MintTaskId();
        var seed = new TaskItem { TagText = NewTaskTagText.Trim() };
        Overlay = new TaskFormViewModel(
            isNew: true,
            id: newId,
            source: seed,
            onSave: form =>
            {
                var task = new TaskItem
                {
                    Id = newId,
                    Order = project.Tasks.Count,
                };
                form.ApplyTo(task);
                project.Tasks.Add(task);
                AfterTasksChanged();
                StatusText = $"Added task {task.Id}";
            },
            onClose: CloseOverlay,
            peers: project.Tasks.ToList());
    }

    private void EditTask(TaskItem? task)
    {
        var project = SelectedProject;
        if (project == null || task == null)
            return;

        Overlay = new TaskFormViewModel(
            isNew: false,
            id: task.Id,
            source: task,
            onSave: form =>
            {
                form.ApplyTo(task);
                AfterTasksChanged();
                StatusText = $"Updated task {task.Id}";
            },
            onClose: CloseOverlay,
            peers: project.Tasks.ToList());
    }

    private void DeleteTask(TaskItem? task)
    {
        var project = SelectedProject;
        if (project == null || task == null)
            return;

        var confirm = MessageBox.Show(
            $"Delete task {task.Id}?",
            "zProject",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        project.Tasks.Remove(task);
        AfterTasksChanged();
        StatusText = $"Deleted task {task.Id}";
    }

    /// <summary>
    /// Moves <paramref name="task"/> so that it sits at <paramref name="indexAmongOthers"/>
    /// in the list once <paramref name="task"/> itself is removed, then persists.
    /// </summary>
    public void MoveTaskToIndex(TaskItem task, int indexAmongOthers)
    {
        var project = SelectedProject;
        if (project == null)
            return;

        var oldIndex = project.Tasks.IndexOf(task);
        if (oldIndex < 0)
            return;

        project.Tasks.RemoveAt(oldIndex);
        var target = Math.Clamp(indexAmongOthers, 0, project.Tasks.Count);
        project.Tasks.Insert(target, task);

        AfterTasksChanged();
        if (target != oldIndex)
            StatusText = $"Moved {task.Id} to position {target + 1}";
    }

    private void ToggleDone(TaskItem? task)
    {
        if (task == null)
            return;
        task.Done = !task.Done;
        if (task.Done)
            task.InProgress = false;
        AfterTasksChanged();
        StatusText = $"{task.Id} marked {(task.Done ? "done" : "not done")}";
    }

    private void ToggleLock(TaskItem? task)
    {
        if (task == null)
            return;
        task.Locked = !task.Locked;
        AfterTasksChanged();
        StatusText = $"{task.Id} {(task.Locked ? "locked — an agent will ignore it" : "unlocked")}";
    }

    private void ToggleCollapsed(TaskItem? task)
    {
        if (task == null)
            return;
        task.Collapsed = !task.Collapsed;
    }

    private void ArchiveDone()
    {
        var project = SelectedProject;
        if (project == null)
            return;
        int n = 0;
        foreach (var t in project.Tasks.Where(t => t.Done && !t.Archived))
        {
            t.Archived = true;
            n++;
        }
        if (n == 0)
            return;
        AfterTasksChanged();
        RefreshCommandStates();
        StatusText = $"Archived {n} completed task(s)";
    }

    /// <summary>Called by the view after a checkbox binding flips Done/InProgress.</summary>
    public void PersistTaskFlagChange(TaskItem task)
    {
        if (task.Done && task.InProgress)
            task.InProgress = false;
        AfterTasksChanged();
        StatusText = $"Updated {task.Id}";
    }

    // ---- Overlay -----------------------------------------------------

    private void CloseOverlay() => Overlay = null;

    private void RefreshCommandStates()
    {
        ReloadProjectCommand.RaiseCanExecuteChanged();
        SaveCurrentCommand.RaiseCanExecuteChanged();
        AddTaskCommand.RaiseCanExecuteChanged();
        ArchiveDoneCommand.RaiseCanExecuteChanged();
        EditLocalDesignCommand.RaiseCanExecuteChanged();
        EditLocalInstructionsCommand.RaiseCanExecuteChanged();
        EditLocalPromptCommand.RaiseCanExecuteChanged();
    }
}
