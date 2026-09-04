using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.WinForms;
using PromptQueue.App.ViewModels;
using PromptQueue.Core.Models;
using WinForms = System.Windows.Forms;

namespace PromptQueue.App.Views;

/// <summary>
/// MainWindow hosts the entire zUI-rendered interface inside a WebView2 control.
/// All data flows over the zUI message bus (zui.send / zui.receive channels).
/// </summary>
public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _wasMinimized;
    private MainViewModel? _hookedVm;
    private WinForms.NotifyIcon? _tray;
    private ZuiHost? _ui;
    private WebView2? _webView;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ── Startup ───────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        await InitWebViewAsync();
    }

    private async System.Threading.Tasks.Task InitWebViewAsync()
    {
        _webView = new WebView2 { Dock = WinForms.DockStyle.Fill };
        WebViewPanel.Controls.Add(_webView);

        // ZuiHost wraps the WebView2 and exposes the zUI message bus.
        _ui = new ZuiHost(_webView);
        // CoreRoot defaults to AppContext.BaseDirectory + "zui" — that's where
        // we copy the zUI core assets via the .csproj ItemGroup. The virtual
        // host maps the folder ABOVE CoreRoot, so "ui/main.html" resolves to
        // AppContext.BaseDirectory/ui/main.html.
        await _ui.InitializeAsync();

        // Register all UI→host message handlers BEFORE loading the page.
        WireHandlers();

        // Load the compiled zUI document.
        await _ui.LoadAsync("ui/main.html");

        // After DOM is ready the UI will send "ui-ready"; we push state then.
    }

    // ── zUI message handlers (UI → host) ─────────────────────────────────

    private void WireHandlers()
    {
        if (_ui == null) return;

        // UI signals it's fully loaded.
        _ui.On("ui-ready", _ => Dispatcher.Invoke(PushFullState));

        // Project actions
        _ui.On("select-project", p => Dispatcher.Invoke(() =>
        {
            var id = p.GetString();
            var proj = Vm?.Workspace.Projects.FirstOrDefault(x =>
                string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
            if (proj != null && Vm != null)
                Vm.SelectedProject = proj;
        }));
        _ui.On("new-project",        _ => Dispatcher.Invoke(() => Vm?.NewProjectCommand.Execute(null)));
        _ui.On("new-local-project",  _ => Dispatcher.Invoke(() => Vm?.NewLocalProjectCommand.Execute(null)));
        _ui.On("save-all",           _ => Dispatcher.Invoke(() => Vm?.SaveAllCommand.Execute(null)));
        _ui.On("save-current",       _ => Dispatcher.Invoke(() => Vm?.SaveCurrentCommand.Execute(null)));
        _ui.On("reload-project",     _ => Dispatcher.Invoke(() => Vm?.ReloadProjectCommand.Execute(null)));
        _ui.On("start-web-server",   _ => Dispatcher.Invoke(() => Vm?.StartWebServerCommand.Execute(null)));
        _ui.On("exit",               _ => Dispatcher.Invoke(() => Vm?.ExitCommand.Execute(null)));
        _ui.On("open-project-dir",   p => Dispatcher.Invoke(() =>
        {
            var id = p.ValueKind == JsonValueKind.Null ? null : p.GetString();
            if (id != null)
            {
                var proj = Vm?.Workspace.Projects.FirstOrDefault(x =>
                    string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
                Vm?.OpenProjectDirectoryCommand.Execute(proj);
            }
            else
            {
                Vm?.OpenProjectDirectoryCommand.Execute(null);
            }
        }));
        _ui.On("remove-project", p => Dispatcher.Invoke(() =>
        {
            var id = p.ValueKind == JsonValueKind.Null ? null : p.GetString();
            if (id != null)
            {
                var proj = Vm?.Workspace.Projects.FirstOrDefault(x =>
                    string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
                Vm?.RemoveProjectCommand.Execute(proj);
            }
            else
            {
                Vm?.RemoveProjectCommand.Execute(null);
            }
        }));

        // Text editing
        _ui.On("edit-global", p => Dispatcher.Invoke(() =>
        {
            var which = p.GetString();
            if (which == "prompt")       Vm?.EditGlobalPromptCommand.Execute(null);
            else if (which == "instructions") Vm?.EditGlobalInstructionsCommand.Execute(null);
            else if (which == "design")  Vm?.EditGlobalDesignCommand.Execute(null);
        }));
        _ui.On("edit-local", p => Dispatcher.Invoke(() =>
        {
            var which = p.GetString();
            if (which == "prompt")       Vm?.EditLocalPromptCommand.Execute(null);
            else if (which == "instructions") Vm?.EditLocalInstructionsCommand.Execute(null);
            else if (which == "design")  Vm?.EditLocalDesignCommand.Execute(null);
        }));
        _ui.On("save-text", p => Dispatcher.Invoke(() =>
        {
            // The text editor channel determines which save action to invoke.
            // Since the overlay logic lives in MainViewModel, we just relay the
            // saved value back through the Overlay's SaveCommand-equivalent.
            // TextEditorViewModel's commit callback stores the text.
            if (Vm?.Overlay is TextEditorViewModel tv)
            {
                tv.Text = p.GetProperty("value").GetString() ?? "";
                tv.SaveCommand.Execute(null);
            }
        }));

        // Task list actions
        _ui.On("add-task",        _ => Dispatcher.Invoke(() => Vm?.AddTaskCommand.Execute(null)));
        _ui.On("open-edit-task",  p => Dispatcher.Invoke(() =>
        {
            var id = p.GetString();
            var task = Vm?.SelectedProject?.Tasks.FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (task != null) Vm?.EditTaskCommand.Execute(task);
        }));
        _ui.On("delete-task", p => Dispatcher.Invoke(() =>
        {
            var id = p.GetString();
            var task = Vm?.SelectedProject?.Tasks.FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (task != null) Vm?.DeleteTaskCommand.Execute(task);
        }));
        _ui.On("toggle-done", p => Dispatcher.Invoke(() =>
        {
            var id = p.GetString();
            var task = Vm?.SelectedProject?.Tasks.FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (task != null) Vm?.ToggleTaskDoneCommand.Execute(task);
        }));
        _ui.On("toggle-lock", p => Dispatcher.Invoke(() =>
        {
            var id = p.GetString();
            var task = Vm?.SelectedProject?.Tasks.FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (task != null) Vm?.ToggleTaskLockCommand.Execute(task);
        }));
        _ui.On("collapse-all", _ => Dispatcher.Invoke(() => Vm?.CollapseAllCommand.Execute(null)));
        _ui.On("expand-all",   _ => Dispatcher.Invoke(() => Vm?.ExpandAllCommand.Execute(null)));
        _ui.On("toggle-subtask", p => Dispatcher.Invoke(() =>
        {
            var taskId = p.GetProperty("taskId").GetString();
            var idx    = p.GetProperty("index").GetInt32();
            var done   = p.GetProperty("done").GetBoolean();
            var task = Vm?.SelectedProject?.Tasks.FirstOrDefault(t =>
                string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (task != null && idx >= 0 && idx < task.Subtasks.Count)
            {
                task.Subtasks[idx].Done = done;
                Vm?.PersistSubtaskChange(task, task.Subtasks[idx]);
            }
        }));

        // Drag-and-drop reorder
        _ui.On("move-task", p => Dispatcher.Invoke(() =>
        {
            var taskId   = p.GetProperty("taskId").GetString();
            var targetId = p.GetProperty("targetId").GetString();
            var above    = p.GetProperty("above").GetBoolean();
            var tasks = Vm?.SelectedProject?.Tasks;
            if (tasks == null) return;
            var task   = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            var target = tasks.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase));
            if (task == null || target == null) return;
            var targetBefore = above ? target : null;
            var targetAfter  = above ? null   : target;
            Vm?.MoveTaskRelative(task, targetBefore, targetAfter);
        }));

        // Save task form
        _ui.On("save-task", p => Dispatcher.Invoke(() => ApplySaveTask(p)));

        // Agent deploys
        _ui.On("deploy-agent", p => Dispatcher.Invoke(() =>
        {
            var agent = p.GetString() ?? "codex";
            if (agent == "codex")        Vm?.DeployCodexCommand.Execute(null);
            else if (agent == "claude")  Vm?.DeployClaudeCommand.Execute(null);
            else if (agent == "agy")     DeployAntigravity();
        }));
        _ui.On("run-task-with-agent", p => Dispatcher.Invoke(() =>
        {
            var taskId = p.GetProperty("taskId").GetString();
            var agent  = p.GetProperty("agent").GetString() ?? "codex";
            var task   = Vm?.SelectedProject?.Tasks.FirstOrDefault(t =>
                string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (agent == "codex")        Vm?.RunTaskWithCodexCommand.Execute(task);
            else if (agent == "claude")  Vm?.RunTaskWithClaudeCommand.Execute(task);
            else if (agent == "agy" && task != null) DeployAntigravityForTask(task);
        }));
        _ui.On("install-cli", p => Dispatcher.Invoke(() =>
        {
            var which = p.GetString();
            if (which == "codex")        Vm?.InstallCodexCliCommand.Execute(null);
            else if (which == "claude")  Vm?.InstallClaudeCliCommand.Execute(null);
            else if (which == "both")    Vm?.InstallBothCliCommand.Execute(null);
        }));

        // Quick-add
        _ui.On("quick-add-begin",  _ => Dispatcher.Invoke(() =>
        {
            if (Vm?.BeginQuickAddCommand.CanExecute(null) == true)
            {
                Vm.BeginQuickAddCommand.Execute(null);
                _ui?.Send("quick-add-state", true);
            }
        }));
        _ui.On("quick-add-commit", p => Dispatcher.Invoke(() =>
        {
            Vm!.QuickAddText = p.GetString() ?? "";
            Vm.CommitQuickAddCommand.Execute(null);
            // Stay in quick-add
            _ui?.Send("quick-add-state", true);
        }));
        _ui.On("quick-add-cancel", _ => Dispatcher.Invoke(() =>
        {
            Vm?.CancelQuickAddCommand.Execute(null);
            _ui?.Send("quick-add-state", false);
        }));

        // Branch selection
        _ui.On("set-branch", p => Dispatcher.Invoke(() =>
        {
            if (Vm != null)
                Vm.NewTaskBranch = p.GetString() ?? "main";
        }));
    }

    // ── Host → UI state pushes ────────────────────────────────────────────

    /// <summary>Push the full project list + selected task list to the UI.</summary>
    private void PushFullState()
    {
        PushProjects();
        PushTasks();
    }

    private void PushProjects()
    {
        if (_ui == null || Vm == null) return;
        var projects = Vm.Workspace.Projects.Select(p => new
        {
            id      = p.Name,
            name    = p.Name,
            isLocal = p.IsLocal,
        }).ToArray();
        _ui.Send("projects", new
        {
            projects,
            selectedProject = Vm.SelectedProject?.Name,
        });
    }

    private void PushTasks()
    {
        if (_ui == null || Vm == null) return;
        var proj = Vm.SelectedProject;
        if (proj == null)
        {
            _ui.Send("tasks", new { tasks = Array.Empty<object>(), knownBranches = new[] { "main" }, knownTags = Array.Empty<string>(), currentBranch = "main" });
            return;
        }

        var tasks = proj.Tasks.Select(t => new
        {
            id           = t.Id,
            name         = t.Name,
            prompt       = t.Prompt,
            requirements = t.Requirements,
            inProgress   = t.InProgress,
            done         = t.Done,
            archived     = t.Archived,
            bug          = t.Bug,
            error        = t.Error,
            errorMessage = t.ErrorMessage,
            locked       = t.Locked,
            commit       = t.Commit,
            build        = t.Build,
            release      = t.Release,
            merge        = t.Merge,
            branch       = t.Branch,
            sectionKey   = t.SectionKey,
            tagText      = t.TagText,
            tags         = t.Tags.ToArray(),
            notes        = t.Notes,
            filesChanged = t.FilesChanged,
            blockedBy    = t.BlockedBy,
            isBlocked    = t.IsBlocked,
            indentLevel  = t.IndentLevel,
            isLocal      = proj.IsLocal,
            subtasks     = t.Subtasks.Select(s => new { text = s.Text, done = s.Done }).ToArray(),
        }).ToArray();

        _ui.Send("tasks", new
        {
            tasks,
            knownBranches = Vm.KnownBranches.ToArray(),
            knownTags     = Vm.KnownTags.ToArray(),
            currentBranch = Vm.NewTaskBranch,
        });
    }

    private void PushStatus(string text)
    {
        _ui?.Send("status", text);
    }

    // ── VM → UI overlay bridging ──────────────────────────────────────────

    /// <summary>
    /// When MainViewModel.Overlay changes to a TaskFormViewModel or TextEditorViewModel,
    /// we translate the VM data to a JSON payload and push it to the zUI overlay.
    /// </summary>
    private void OnOverlayChanged()
    {
        var vm = Vm;
        if (vm == null) return;

        if (vm.Overlay is TaskFormViewModel form)
        {
            var proj = vm.SelectedProject;
            var payload = new
            {
                isNew        = form.IsNew,
                id           = form.Id,
                name         = form.Name,
                branch       = form.Branch,
                inProgress   = form.InProgress,
                done         = form.Done,
                bug          = form.Bug,
                locked       = form.Locked,
                archived     = form.Archived,
                commit       = form.Commit,
                build        = form.Build,
                release      = form.Release,
                merge        = form.Merge,
                blockedBy    = form.BlockedBy,
                tagText      = form.TagText,
                prompt       = form.Prompt,
                requirements = form.Requirements,
                notes        = form.Notes,
                filesChanged = form.FilesChanged,
                errorMessage = form.ErrorMessage,
                hasNotes         = form.HasNotes,
                hasFilesChanged  = form.HasFilesChanged,
                hasError         = form.HasError,
                knownBranches    = form.KnownBranches.ToArray(),
                knownTags        = form.KnownTags.ToArray(),
                blockCandidates  = form.BlockCandidates.ToArray(),
                subtasks = form.Subtasks.Select(s => new { text = s.Text, done = s.Done }).ToArray(),
            };
            _ui?.Send("open-task-form", payload);
        }
        else if (vm.Overlay is TextEditorViewModel tv)
        {
            _ui?.Send("open-text-editor", new
            {
                title    = tv.Title,
                subtitle = tv.Subtitle,
                current  = tv.Text,
                channel  = "text-editor",
            });
        }
        else if (vm.Overlay == null)
        {
            _ui?.Send("close-overlay", (object?)null);
        }
    }

    /// <summary>Apply the JSON payload from the save-task message to the open TaskFormViewModel.</summary>
    private void ApplySaveTask(JsonElement p)
    {
        if (Vm?.Overlay is TaskFormViewModel form)
        {
            form.Name         = p.GetProperty("name").GetString()         ?? "";
            form.Branch       = p.GetProperty("branch").GetString()        ?? "main";
            form.InProgress   = p.GetProperty("inProgress").GetBoolean();
            form.Done         = p.GetProperty("done").GetBoolean();
            form.Bug          = p.GetProperty("bug").GetBoolean();
            form.Locked       = p.GetProperty("locked").GetBoolean();
            form.Archived     = p.GetProperty("archived").GetBoolean();
            form.Commit       = p.GetProperty("commit").GetBoolean();
            form.Build        = p.GetProperty("build").GetBoolean();
            form.Release      = p.GetProperty("release").GetBoolean();
            form.Merge        = p.GetProperty("merge").GetBoolean();
            form.BlockedBy    = p.GetProperty("blockedBy").GetString()     ?? "";
            form.TagText      = p.GetProperty("tagText").GetString()       ?? "";
            form.Prompt       = p.GetProperty("prompt").GetString()        ?? "";
            form.Requirements = p.GetProperty("requirements").GetString()  ?? "";
            form.Notes        = p.GetProperty("notes").GetString()         ?? "";
            form.FilesChanged = p.GetProperty("filesChanged").GetString()  ?? "";
            form.ErrorMessage = p.GetProperty("errorMessage").GetString()  ?? "";

            // Subtasks
            form.Subtasks.Clear();
            if (p.TryGetProperty("subtasks", out var stsEl) && stsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in stsEl.EnumerateArray())
                    form.Subtasks.Add(new PromptQueue.Core.Models.Subtask
                    {
                        Text = s.GetProperty("text").GetString() ?? "",
                        Done = s.GetProperty("done").GetBoolean(),
                    });
            }

            // Trigger save and close overlay
            if (form.SaveCommand.CanExecute(null))
                form.SaveCommand.Execute(null);
        }
    }

    // ── Antigravity deployment ────────────────────────────────────────────

    private void DeployAntigravity()
    {
        var project = Vm?.SelectedProject;
        if (project == null) return;
        var branch = Vm?.CurrentBranch ?? "main";
        LaunchAgentProcess("agy",
            $"--dangerous-mode read prompt.txt in this directory and follow it exactly for branch '{branch}'. Work through all tasks on that branch.",
            project);
    }

    private void DeployAntigravityForTask(TaskItem task)
    {
        var project = Vm?.SelectedProject;
        if (project == null) return;
        LaunchAgentProcess("agy",
            $"--dangerous-mode read prompt.txt in this directory and follow it exactly for task {task.Id} on branch '{task.Branch}' only. Complete ONLY task {task.Id}, then stop.",
            project);
    }

    private void LaunchAgentProcess(string exe, string args, PromptQueue.Core.Models.Project project)
    {
        if (!Directory.Exists(project.Directory))
        {
            MessageBox.Show($"The project directory does not exist:\n{project.Directory}", "zProject");
            return;
        }
        try
        {
            var dir = project.Directory.Replace("'", "''");
            var escapedArgs = args.Replace("'", "''");
            var psCmd = $"Set-Location -LiteralPath '{dir}'; & {exe} {escapedArgs}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                UseShellExecute = true,
                WorkingDirectory = project.Directory,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "zProject", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── ViewModel property change monitoring ──────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hookedVm != null)
        {
            _hookedVm.ExitRequested        -= OnExitRequested;
            _hookedVm.PropertyChanged      -= OnVmPropertyChanged;
        }

        _hookedVm = Vm;

        if (_hookedVm != null)
        {
            _hookedVm.ExitRequested   += OnExitRequested;
            _hookedVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.TasksView):
            case nameof(MainViewModel.KnownBranches):
            case nameof(MainViewModel.KnownTags):
                Dispatcher.Invoke(() => { PushTasks(); });
                break;

            case nameof(MainViewModel.SelectedProject):
                Dispatcher.Invoke(() => { PushProjects(); PushTasks(); });
                break;

            case nameof(MainViewModel.StatusText):
                Dispatcher.Invoke(() => PushStatus(Vm?.StatusText ?? ""));
                break;

            case nameof(MainViewModel.Overlay):
                Dispatcher.Invoke(OnOverlayChanged);
                break;

            case nameof(MainViewModel.QuickAddActive):
                Dispatcher.Invoke(() => _ui?.Send("quick-add-state", Vm?.QuickAddActive ?? false));
                break;

            case nameof(MainViewModel.HasSelectedProject):
                Dispatcher.Invoke(() => { PushProjects(); });
                break;
        }
    }

    private void OnExitRequested()
    {
        _allowClose = true;
        Close();
    }

    // ── Window lifecycle (tray, hide/show) ────────────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized)
        {
            if (_wasMinimized)
                Vm?.ReloadAllProjects();
            _wasMinimized = false;
        }
        else
        {
            _wasMinimized = true;
        }
    }

    /// <summary>Restore from the system tray or from a second-instance activation request (ZP-55).</summary>
    public void RestoreFromExternalRequest()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Vm?.ReloadAllProjects();
    }

    private void RestoreFromTray() => RestoreFromExternalRequest();

    private void SetupTray()
    {
        if (_tray != null) return;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open",  null, (_, _) => RestoreFromTray());
        menu.Items.Add("Close", null, (_, _) => { _allowClose = true; Close(); });

        _tray = new WinForms.NotifyIcon
        {
            Text             = "zProject",
            Icon             = LoadTrayIcon(),
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = Application.GetResourceStream(uri)!.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }
}
