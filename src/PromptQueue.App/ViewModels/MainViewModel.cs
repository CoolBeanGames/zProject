using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using PromptQueue.Core.Models;
using PromptQueue.Core.Operator;
using PromptQueue.Core.Serialization;
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
    private bool _archivedCollapsed = true;   // ZP-53: archived list starts collapsed
    private string _newTaskTagText = "";
    private bool _quickAddActive;
    private string _quickAddText = "";

    public MainViewModel(Workspace workspace)
    {
        Workspace = workspace;

        NewProjectCommand = new RelayCommand(NewProject);
        SaveAllCommand = new RelayCommand(SaveAll);
        SaveCurrentCommand = new RelayCommand(SaveCurrentProject, () => SelectedProject != null);
        ReloadProjectCommand = new RelayCommand(ReloadProject, () => SelectedProject != null);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        StartWebServerCommand = new RelayCommand(StartWebServer);

        InstallCodexCliCommand = new RelayCommand(() => RunInTerminal(
            "npm install -g @openai/codex", "Install ChatGPT / Codex CLI"));
        InstallClaudeCliCommand = new RelayCommand(() => RunInTerminal(
            "npm install -g @anthropic-ai/claude-code", "Install Claude Code"));
        InstallBothCliCommand = new RelayCommand(() => RunInTerminal(
            "npm install -g @openai/codex @anthropic-ai/claude-code", "Install both AI CLIs"));

        DeployCodexCommand = new RelayCommand(() => DeployAgent("codex"), HasProject);
        DeployClaudeCommand = new RelayCommand(() => DeployAgent("claude"), HasProject);
        RunTaskWithCodexCommand = new RelayCommand(p => DeployAgentForTask("codex", p as TaskItem));
        RunTaskWithClaudeCommand = new RelayCommand(p => DeployAgentForTask("claude", p as TaskItem));

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
        CollapseAllCommand = new RelayCommand(() => SetAllCollapsed(true), HasProject);
        ExpandAllCommand = new RelayCommand(() => SetAllCollapsed(false), HasProject);
        ToggleCompletedCollapsedCommand = new RelayCommand(() => CompletedCollapsed = !CompletedCollapsed);
        ToggleArchivedCollapsedCommand = new RelayCommand(() => ArchivedCollapsed = !ArchivedCollapsed);

        BeginQuickAddCommand = new RelayCommand(() => QuickAddActive = true, HasProject);
        CommitQuickAddCommand = new RelayCommand(CommitQuickAdd);
        CancelQuickAddCommand = new RelayCommand(() => { QuickAddActive = false; QuickAddText = ""; });

        if (Workspace.Projects.Count > 0)
            SelectedProject = Workspace.Projects[0];

        // Auto-reload the open project from disk every 120s (ZP-43).
        _autoReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        _autoReloadTimer.Tick += (_, _) => AutoReloadTick();
        _autoReloadTimer.Start();
    }

    private readonly DispatcherTimer _autoReloadTimer;

    /// <summary>
    /// Called once after the main window is up: if any project's tasks.xml
    /// failed to parse, tell the user which one and where, rather than letting
    /// a single bad file crash startup (ZP-52 fallout). The project still opens
    /// with an empty list and its file is left untouched on disk.
    /// </summary>
    public void WarnAboutLoadErrors()
    {
        var broken = Workspace.Projects.Where(p => p.HasLoadError).ToList();
        if (broken.Count == 0)
            return;

        var lines = broken.Select(p =>
            $"• {p.Name}\n    {Path.Combine(p.Directory, TaskXmlSerializer.FileName)}\n    {p.LoadError}");

        MessageBox.Show(
            "These projects have a tasks.xml that could not be read. They are "
            + "shown with an empty task list and their file has been left as-is "
            + "so you can fix it by hand (usually an unescaped < > or & inside "
            + "<notes>). Use Reload once fixed.\n\n"
            + string.Join("\n\n", lines),
            "zProject", MessageBoxButton.OK, MessageBoxImage.Warning);

        StatusText = $"{broken.Count} project(s) have an unreadable tasks.xml";
    }

    private void AutoReloadTick()
    {
        // Skip while an editor / task form is open so we don't discard edits.
        if (SelectedProject == null || Overlay != null)
            return;
        ProjectStore.ReloadInto(SelectedProject);
        RebuildTasksView();
        StatusText = $"Auto-reloaded \"{SelectedProject.Name}\" ({DateTime.Now:HH:mm})";
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

    /// <summary>Whether the Archived section is collapsed (hidden) in the list (ZP-53).</summary>
    public bool ArchivedCollapsed
    {
        get => _archivedCollapsed;
        set => Set(ref _archivedCollapsed, value);
    }

    public RelayCommand ToggleArchivedCollapsedCommand { get; }

    /// <summary>Every distinct tag used anywhere in the selected project (for autocomplete).</summary>
    public ObservableCollection<string> KnownTags { get; } = new();

    /// <summary>The header "tags" field — applied to newly created tasks.</summary>
    public string NewTaskTagText
    {
        get => _newTaskTagText;
        set => Set(ref _newTaskTagText, value);
    }

    // ---- Quick add (ZP-57): press Space -> type a name -> Enter ----

    /// <summary>Whether the inline "type a name, press Enter" quick-add bar is showing.</summary>
    public bool QuickAddActive
    {
        get => _quickAddActive;
        set
        {
            if (Set(ref _quickAddActive, value) && !value)
                QuickAddText = "";
        }
    }

    /// <summary>The name being typed into the quick-add bar.</summary>
    public string QuickAddText
    {
        get => _quickAddText;
        set => Set(ref _quickAddText, value);
    }

    public RelayCommand BeginQuickAddCommand { get; }
    public RelayCommand CommitQuickAddCommand { get; }
    public RelayCommand CancelQuickAddCommand { get; }

    private void CommitQuickAdd()
    {
        var project = SelectedProject;
        var name = QuickAddText.Trim();
        if (project == null || name.Length == 0)
        {
            QuickAddActive = false;
            return;
        }

        ApplyViaOperator(
            p =>
            {
                var created = OperatorEngine.NewTask(p.Name, name);
                if (created.Ok && !string.IsNullOrEmpty(created.Output))
                    OperatorEngine.Sync(created.Output, "locked", "true");   // ZP-57: locked by default
                return created;
            },
            r => r.Ok ? $"Added task {r.Output}" : $"Operator: {r.Message}");

        // Stay in quick-add so several tasks can be entered in a row.
        QuickAddText = "";
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
    public RelayCommand StartWebServerCommand { get; }
    public RelayCommand InstallCodexCliCommand { get; }
    public RelayCommand InstallClaudeCliCommand { get; }
    public RelayCommand InstallBothCliCommand { get; }
    public RelayCommand DeployCodexCommand { get; }
    public RelayCommand DeployClaudeCommand { get; }
    public RelayCommand RunTaskWithCodexCommand { get; }
    public RelayCommand RunTaskWithClaudeCommand { get; }

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
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ExpandAllCommand { get; }

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

    /// <summary>
    /// The Reload button / menu item: re-reads ONLY the currently selected
    /// project's files from disk (ZP-35). Other open projects are untouched.
    /// </summary>
    private void ReloadProject()
    {
        var project = SelectedProject;
        if (project == null)
            return;
        ProjectStore.ReloadInto(project);
        RebuildTasksView();
        Workspace.SaveDataCfg();
        StatusText = $"Reloaded only \"{project.Name}\" from disk";
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
        RebuildTasksView();   // completed tasks may have been auto-archived (ZP-47)
        StatusText = $"Saved workspace and {Workspace.Projects.Count} project(s)";
    }

    /// <summary>
    /// Launches the read-only web view server (ZP-38). It is a console app, so it
    /// gets its own window; the workspace it serves is the same one this app uses.
    /// </summary>
    private void StartWebServer()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "zProject_server.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(
                "zProject_server.exe was not found next to the app.\n\n" +
                "Build the PromptQueue.Server project into the same folder.",
                "zProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,   // gives the console app its own window
                WorkingDirectory = AppContext.BaseDirectory,
            });
            StatusText = "Started the read-only web view server (see its console window)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "zProject", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Opens a new terminal window and runs <paramref name="command"/> (ZP-41).</summary>
    private void RunInTerminal(string command, string title)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k title {title} && echo {title} && echo. && {command}",
                UseShellExecute = true,
            });
            StatusText = $"Running: {command}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "zProject", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Opens a terminal in the selected project's directory and starts the agent
    /// pointed at its prompt.txt so it works the task queue (ZP-42).
    /// </summary>
    private void DeployAgent(string agent)
        => LaunchAgent(agent,
            "Read prompt.txt in this directory and follow it exactly, then work the task queue.",
            p => $"Deployed {agent} to \"{p.Name}\"");

    /// <summary>
    /// "Run only this with Codex / Claude Code" from a task's right-click menu
    /// (ZP-52): points the agent at prompt.txt for one task id only.
    /// </summary>
    private void DeployAgentForTask(string agent, TaskItem? task)
    {
        if (task == null)
            return;
        LaunchAgent(agent,
            $"Read prompt.txt in this directory and follow it exactly for task {task.Id} only. " +
            $"Complete ONLY task {task.Id}, then stop.",
            _ => $"Running {agent} on {task.Id} only");
    }

    private void LaunchAgent(string agent, string boot, Func<Project, string> status)
    {
        var project = SelectedProject;
        if (project == null)
            return;
        if (!Directory.Exists(project.Directory))
        {
            MessageBox.Show($"The project directory does not exist:\n{project.Directory}",
                "zProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // ZP-56: deploy the agent through PowerShell instead of cmd.exe.
            var dir = project.Directory.Replace("'", "''");
            var bootArg = boot.Replace("'", "''");
            var psCommand = $"Set-Location -LiteralPath '{dir}'; & {agent} '{bootArg}'";

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                UseShellExecute = true,
                WorkingDirectory = project.Directory,
            });
            StatusText = status(project);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not start '{agent}'. Is it installed and on PATH?\n\n{ex.Message}",
                "zProject", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCurrentProject()
    {
        if (SelectedProject == null)
            return;
        ProjectStore.Save(SelectedProject);
        Workspace.SaveDataCfg();
        RebuildTasksView();   // completed tasks may have been auto-archived (ZP-47)
        StatusText = $"Saved project \"{SelectedProject.Name}\"";
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
        ResolveImagePaths();
        RecomputeBlockedLayout();

        var view = new ListCollectionView(SelectedProject.Tasks)
        {
            CustomSort = new TaskDisplayComparer(),
        };
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

    /// <summary>
    /// Routes a task change through the operator (ZP-65) — the single writer of
    /// tasks.xml — then re-reads the project from disk so the app shows exactly
    /// what was persisted, even if an agent changed other tasks in the meantime.
    /// Card collapse state (runtime-only) is carried across the reload.
    /// </summary>
    private void ApplyViaOperator(Func<Project, OperatorResult> op, Func<OperatorResult, string> status)
    {
        var project = SelectedProject;
        if (project == null || project.HasLoadError)
            return;

        OperatorResult result;
        try
        {
            result = op(project);
        }
        catch (Exception ex)
        {
            StatusText = $"Operator error: {ex.Message}";
            return;
        }

        var collapsed = project.Tasks.Where(t => t.Collapsed)
            .Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ProjectStore.ReloadInto(project);
        foreach (var t in project.Tasks)
            if (collapsed.Contains(t.Id))
                t.Collapsed = true;

        RebuildTasksView();
        Workspace.SaveDataCfg();
        StatusText = status(result);
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

    /// <summary>
    /// Resolves each task's runtime <see cref="TaskItem.ImagePath"/> from its
    /// <see cref="TaskItem.Image"/> file name and the project's task_images folder
    /// (ZP-59), so the card can show a thumbnail.
    /// </summary>
    private void ResolveImagePaths()
    {
        var project = SelectedProject;
        if (project == null)
            return;
        foreach (var t in project.Tasks)
        {
            if (!t.HasImage)
            {
                t.ImagePath = "";
                continue;
            }
            var path = Path.Combine(
                project.Directory, TaskFormViewModel.ImageFolderName, t.Image);
            t.ImagePath = File.Exists(path) ? path : "";
        }
    }

    /// <summary>
    /// Recomputes each task's runtime DisplayOrder / IndentLevel so a blocked
    /// card sits directly under its blocker, indented (ZP-37). Order (the xml
    /// position) is never touched, so an unblocked card snaps back.
    /// </summary>
    private void RecomputeBlockedLayout()
    {
        var project = SelectedProject;
        if (project == null)
            return;

        var byId = project.Tasks
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var t in project.Tasks)
        {
            t.DisplayOrder = t.Order;
            t.IndentLevel = 0;
        }

        // A few passes settle short blocker chains; a cycle just stops early.
        for (int pass = 0; pass < 6; pass++)
        {
            bool changed = false;
            foreach (var t in project.Tasks)
            {
                if (!t.IsBlocked ||
                    !byId.TryGetValue(t.BlockedBy.Trim(), out var blocker) ||
                    ReferenceEquals(blocker, t))
                    continue;

                var siblings = project.Tasks
                    .Where(x => x.IsBlocked &&
                                string.Equals(x.BlockedBy.Trim(), blocker.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Order)
                    .ToList();
                var idx = siblings.IndexOf(t);

                var newDisplay = blocker.DisplayOrder + 0.0001 * (idx + 1);
                var newIndent = blocker.IndentLevel + 1;

                if (Math.Abs(newDisplay - t.DisplayOrder) > 1e-9 || newIndent != t.IndentLevel)
                {
                    t.DisplayOrder = newDisplay;
                    t.IndentLevel = newIndent;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }
    }

    private sealed class TaskDisplayComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not TaskItem a || y is not TaskItem b)
                return 0;
            var s = a.SectionRank.CompareTo(b.SectionRank);
            return s != 0 ? s : a.DisplayOrder.CompareTo(b.DisplayOrder);
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
                var task = new TaskItem { Id = newId, Order = project.Tasks.Count };
                form.ApplyTo(task);
                ApplyViaOperator(
                    p =>
                    {
                        // Mint the id through the operator so its NextIndex on disk
                        // advances even against a concurrently running agent.
                        var created = OperatorEngine.NewTask(p.Name, task.Name, task.Prompt);
                        if (created.Ok && !string.IsNullOrEmpty(created.Output))
                        {
                            task.Id = created.Output;
                            return OperatorEngine.Upsert(p.Name, task);
                        }
                        return created;
                    },
                    r => r.Ok ? $"Added task {task.Id}" : $"Operator: {r.Message}");
            },
            onClose: CloseOverlay,
            peers: project.Tasks.ToList(),
            projectDirectory: project.Directory);
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
                ApplyViaOperator(
                    p => OperatorEngine.Upsert(p.Name, task),
                    r => r.Ok ? $"Updated task {task.Id}" : $"Operator: {r.Message}");
            },
            onClose: CloseOverlay,
            peers: project.Tasks.ToList(),
            projectDirectory: project.Directory);
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

        ApplyViaOperator(
            _ => OperatorEngine.Delete(task.Id),
            r => r.Ok ? $"Deleted task {task.Id}" : $"Operator: {r.Message}");
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

        var target = Math.Clamp(indexAmongOthers, 0, Math.Max(0, project.Tasks.Count - 1));
        if (target == oldIndex)
            return;

        ApplyViaOperator(
            _ => OperatorEngine.Move(task.Id, target),
            r => r.Ok ? $"Moved {task.Id} to position {target + 1}" : $"Operator: {r.Message}");
    }

    private void ToggleDone(TaskItem? task)
    {
        if (task == null)
            return;
        var newDone = !task.Done;
        ApplyViaOperator(
            _ =>
            {
                var r = OperatorEngine.Sync(task.Id, "done", newDone ? "true" : "false");
                if (newDone)
                    OperatorEngine.Sync(task.Id, "inProgress", "false");
                return r;
            },
            _ => $"{task.Id} marked {(newDone ? "done" : "not done")}");
    }

    private void ToggleLock(TaskItem? task)
    {
        if (task == null)
            return;
        var newLocked = !task.Locked;
        ApplyViaOperator(
            _ => OperatorEngine.Sync(task.Id, "locked", newLocked ? "true" : "false"),
            _ => $"{task.Id} {(newLocked ? "locked — an agent will ignore it" : "unlocked")}");
    }

    private void ToggleCollapsed(TaskItem? task)
    {
        if (task == null)
            return;
        task.Collapsed = !task.Collapsed;
    }

    /// <summary>Collapses or expands every card in the selected project (ZP-50).</summary>
    private void SetAllCollapsed(bool collapsed)
    {
        var project = SelectedProject;
        if (project == null)
            return;
        foreach (var t in project.Tasks)
            t.Collapsed = collapsed;
        StatusText = collapsed ? "Collapsed all cards" : "Expanded all cards";
    }

    /// <summary>Called by the view after a checkbox binding flips Done/InProgress.</summary>
    public void PersistTaskFlagChange(TaskItem task)
    {
        var done = task.Done;
        ApplyViaOperator(
            _ =>
            {
                var r = OperatorEngine.Sync(task.Id, "done", done ? "true" : "false");
                if (done)
                    OperatorEngine.Sync(task.Id, "inProgress", "false");
                return r;
            },
            _ => $"Updated {task.Id}");
    }

    /// <summary>Called by the view after a subtask checkbox is toggled on a card.</summary>
    public void PersistSubtaskChange(TaskItem task, Subtask sub)
    {
        var index = task.Subtasks.IndexOf(sub);
        if (index < 0)
            return;
        var done = sub.Done;
        ApplyViaOperator(
            _ => OperatorEngine.SetSubtaskDone(task.Id, index, done),
            _ => $"{task.Id} subtask {index + 1} {(done ? "done" : "not done")}");
    }

    // ---- Overlay -----------------------------------------------------

    private void CloseOverlay() => Overlay = null;

    private void RefreshCommandStates()
    {
        ReloadProjectCommand.RaiseCanExecuteChanged();
        SaveCurrentCommand.RaiseCanExecuteChanged();
        AddTaskCommand.RaiseCanExecuteChanged();
        CollapseAllCommand.RaiseCanExecuteChanged();
        ExpandAllCommand.RaiseCanExecuteChanged();
        EditLocalDesignCommand.RaiseCanExecuteChanged();
        EditLocalInstructionsCommand.RaiseCanExecuteChanged();
        EditLocalPromptCommand.RaiseCanExecuteChanged();
    }
}
