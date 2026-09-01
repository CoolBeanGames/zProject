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

        var view = new ListCollectionView(SelectedProject.Tasks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskItem.SectionKey)));
        _tasksView = view;
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
        _tasksView?.Refresh();
        SaveCurrent();
        Workspace.Save();
    }

    // ---- Tasks -------------------------------------------------------

    private void AddTask()
    {
        var project = SelectedProject;
        if (project == null)
            return;

        var newId = project.MintTaskId();
        Overlay = new TaskFormViewModel(
            isNew: true,
            id: newId,
            source: null,
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
            onClose: () =>
            {
                CloseOverlay();
            });
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
            onClose: CloseOverlay);
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
        AddTaskCommand.RaiseCanExecuteChanged();
        EditLocalDesignCommand.RaiseCanExecuteChanged();
        EditLocalInstructionsCommand.RaiseCanExecuteChanged();
        EditLocalPromptCommand.RaiseCanExecuteChanged();
    }
}
