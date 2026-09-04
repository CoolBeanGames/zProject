using System.Globalization;
using System.Xml.Linq;
using PromptQueue.Core.Models;
using PromptQueue.Core.Serialization;
using PromptQueue.Core.Storage;

namespace PromptQueue.Core.Operator;

/// <summary>
/// The "operator" (ZP-65): the single choke-point through which the app, the
/// AI agents and the web server all mutate a project's <c>tasks.xml</c>, so
/// nobody's write clobbers anybody else's while an agent is running.
///
/// <para>
/// Every mutating call is turned into a <see cref="OperatorJob"/>, appended to a
/// persistent queue file, and then the whole queue is drained under a single
/// cross-process mutex. A job is removed from the queue only once it has been
/// applied and the affected project saved, so a crash mid-drain simply retries
/// the outstanding jobs on the next call. Read-only calls skip the queue.
/// </para>
///
/// <para>
/// The engine runs in-process (the WPF app calls it directly) and out of
/// process (<c>zProject_operator.exe</c> calls the same methods); both share the
/// same mutex and queue file, so they serialise against each other.
/// </para>
/// </summary>
public static class OperatorEngine
{
    /// <summary>Name of the system-wide mutex that serialises all tasks.xml writes.</summary>
    public const string MutexName = @"Global\zProject.Operator";

    /// <summary>Folder (under the workspace root) that holds the queue file.</summary>
    public static string QueueDirectory =>
        Path.Combine(Workspace.DefaultRootDirectory, "operator");

    /// <summary>The queue file itself: the list of pending jobs.</summary>
    public static string QueuePath => Path.Combine(QueueDirectory, "queue.xml");

    // ---- read (no queue) --------------------------------------------------

    /// <summary>Returns a verbatim copy of a project's <c>tasks.xml</c>.</summary>
    public static OperatorResult Read(string projectRef)
    {
        var ws = Workspace.Load();
        var project = ResolveProject(ws, projectRef);
        if (project == null)
            return OperatorResult.Fail($"No project matches \"{projectRef}\".");

        var path = Path.Combine(project.Directory, TaskXmlSerializer.FileName);
        if (!File.Exists(path))
            return OperatorResult.Fail($"{path} does not exist yet.");

        return OperatorResult.Pass($"Read {project.Name}", File.ReadAllText(path));
    }

    /// <summary>Lists every known project (name + directory), newline separated.</summary>
    public static OperatorResult List()
    {
        var ws = Workspace.Load();
        var lines = ws.Projects.Select(p => $"{p.Name}\t{p.Directory}");
        return OperatorResult.Pass($"{ws.Projects.Count} project(s)", string.Join("\n", lines));
    }

    /// <summary>Returns the combined global and local instructions for a project.</summary>
    public static OperatorResult Instructions(string projectRef)
    {
        var ws = Workspace.Load();
        var project = ResolveProject(ws, projectRef);
        if (project == null)
            return OperatorResult.Fail($"No project matches \"{projectRef}\".");

        var text = (ws.GlobalInstructions + "\n\n" + project.LocalInstructions).Trim();
        return OperatorResult.Pass($"Instructions for {project.Name}", text);
    }

    /// <summary>Returns a project's whole <c>archive.xml</c> (ZP-72). Empty when there is none.</summary>
    public static OperatorResult GetArchive(string projectRef)
    {
        var ws = Workspace.Load();
        var project = ResolveProject(ws, projectRef);
        if (project == null)
            return OperatorResult.Fail($"No project matches \"{projectRef}\".");

        var path = Path.Combine(project.Directory, TaskXmlSerializer.ArchiveFileName);
        return File.Exists(path)
            ? OperatorResult.Pass($"Archive of {project.Name}", File.ReadAllText(path))
            : OperatorResult.Pass($"{project.Name} has no archive yet",
                $"<tasks project=\"{project.Name}\" nextIndex=\"1\" />");
    }

    /// <summary>Returns one field's value for a task (ZP-72), or fails if the field is unknown.</summary>
    public static OperatorResult GetTag(string taskId, string field)
    {
        var ws = Workspace.Load();
        var (_, task) = ResolveTask(ws, taskId);
        if (task == null)
            return OperatorResult.Fail($"No task \"{taskId}\".");
        var value = TaskFields.Read(task, field);
        return value == null
            ? OperatorResult.Fail($"Unknown field \"{field}\".")
            : OperatorResult.Pass($"{task.Id}.{field}", value);
    }

    // ---- mutations (queued) ---------------------------------------------

    /// <summary>
    /// Queues an override of a single field on a task, e.g.
    /// <c>sync ZP-52 done true</c>. Field name matches the tasks.xml element
    /// (case-insensitive); a handful of friendly aliases are also accepted.
    /// </summary>
    public static OperatorResult Sync(string taskId, string field, string value)
        => Enqueue(new OperatorJob("sync", taskId, field, value));

    /// <summary>Queues creation of a new task; the new id is returned in the output.</summary>
    public static OperatorResult NewTask(string projectRef, string name, string prompt = "")
        => Enqueue(new OperatorJob("new_task", projectRef, name, prompt));

    /// <summary>
    /// Queues creation of a new project. With no directory it is created under
    /// <c>&lt;workspace root&gt;/projects/&lt;name&gt;</c>. The directory is returned in the output.
    /// </summary>
    public static OperatorResult NewProject(string name, string? directory = null)
        => Enqueue(new OperatorJob("new_project", name, directory ?? ""));

    /// <summary>Queues creation of a "local project" (ZP-70) — a personal to-do list, no path.</summary>
    public static OperatorResult NewLocalProject(string name)
        => Enqueue(new OperatorJob("new_local_project", name));

    /// <summary>Queues several field overrides on one task in a single job.</summary>
    public static OperatorResult SyncMany(string taskId, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var job = new OperatorJob("sync_many", taskId);
        foreach (var kv in fields)
        {
            job.Args.Add(kv.Key);
            job.Args.Add(kv.Value);
        }
        return Enqueue(job);
    }

    /// <summary>Queues a new subtask (checklist line) onto an existing task.</summary>
    public static OperatorResult NewSubtask(string taskId, string text)
        => Enqueue(new OperatorJob("new_subtask", taskId, text));

    /// <summary>Queues a done/not-done change for one subtask (by zero-based index).</summary>
    public static OperatorResult SetSubtaskDone(string taskId, int index, bool done)
        => Enqueue(new OperatorJob("subtask_done", taskId,
            index.ToString(CultureInfo.InvariantCulture), done ? "true" : "false"));

    /// <summary>Queues "send this task to the archive" (ZP-72): done + archived, out of the queue.</summary>
    public static OperatorResult Archive(string taskId)
        => Enqueue(new OperatorJob("archive", taskId));

    /// <summary>Queues an agent ownership lock on a task (ZP-72): sets lockKey if free.</summary>
    public static OperatorResult AgentLock(string taskId, string key)
        => Enqueue(new OperatorJob("agent_lock", taskId, key));

    /// <summary>Queues release of an agent lock (ZP-72): clears lockKey if the key matches.</summary>
    public static OperatorResult AgentUnlock(string taskId, string key)
        => Enqueue(new OperatorJob("agent_unlock", taskId, key));

    /// <summary>Queues removal of a task.</summary>
    public static OperatorResult Delete(string taskId)
        => Enqueue(new OperatorJob("delete", taskId));

    /// <summary>Queues a move of a task to a new zero-based position in its project.</summary>
    public static OperatorResult Move(string taskId, int index)
        => Enqueue(new OperatorJob("move", taskId, index.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Queues a full replace of a task (matched by id) with the supplied one, or
    /// an add when no task with that id exists. Used by the app, which edits a
    /// task in memory and then hands the finished object to the operator.
    /// </summary>
    public static OperatorResult Upsert(string projectRef, TaskItem task)
    {
        var job = new OperatorJob("upsert", projectRef, task.Id)
        {
            Payload = SerializeTask(task),
        };
        return Enqueue(job);
    }

    // ---- queue plumbing -----------------------------------------------

    private static OperatorResult Enqueue(OperatorJob job)
    {
        using var guard = new CrossProcessLock(MutexName);

        var queue = LoadQueue();
        queue.Add(job);
        SaveQueue(queue);

        return Drain(queue);
    }

    /// <summary>
    /// Applies every queued job in order, saving each affected project and
    /// removing the job from the queue as it succeeds. Called while the mutex is
    /// held. Returns the result of the last job (the caller's own).
    /// </summary>
    private static OperatorResult Drain(List<OperatorJob> queue)
    {
        var ws = Workspace.Load();
        var touched = new HashSet<Project>();
        OperatorResult last = OperatorResult.Pass("Nothing to do");

        while (queue.Count > 0)
        {
            var job = queue[0];
            try
            {
                last = Apply(ws, job, touched);
            }
            catch (Exception ex)
            {
                last = OperatorResult.Fail($"{job.Command} failed: {ex.Message}");
            }

            queue.RemoveAt(0);
            SaveQueue(queue);
        }

        foreach (var project in touched)
            ProjectStore.Save(project);
        if (touched.Count > 0)
            ws.SaveDataCfg();

        return last;
    }

    private static OperatorResult Apply(Workspace ws, OperatorJob job, HashSet<Project> touched)
    {
        switch (job.Command)
        {
            case "sync":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                if (!TaskFields.Apply(task, job.Arg(1), job.Arg(2)))
                    return OperatorResult.Fail($"Unknown field \"{job.Arg(1)}\".");
                touched.Add(project!);
                return OperatorResult.Pass($"{task.Id}.{job.Arg(1)} = {job.Arg(2)}");
            }

            case "sync_many":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                int applied = 0;
                for (int i = 1; i + 1 < job.Args.Count; i += 2)
                    if (TaskFields.Apply(task, job.Args[i], job.Args[i + 1]))
                        applied++;
                touched.Add(project!);
                return OperatorResult.Pass($"{task.Id}: {applied} field(s) updated");
            }

            case "new_project":
            {
                var name = job.Arg(0).Trim();
                if (name.Length == 0)
                    return OperatorResult.Fail("A project name is required.");
                var dir = job.Arg(1).Trim();
                if (dir.Length == 0)
                    dir = Path.Combine(Workspace.DefaultRootDirectory, "projects", SanitizeName(name));
                var project = ws.AddProject(dir);   // creates dir, registers, seeds globals, saves
                return OperatorResult.Pass($"Project \"{project.Name}\" at {project.Directory}", project.Directory);
            }

            case "new_local_project":
            {
                var name = job.Arg(0).Trim();
                if (name.Length == 0)
                    return OperatorResult.Fail("A name is required.");
                var project = ws.AddLocalProject(name);
                return OperatorResult.Pass($"Local project \"{project.Name}\"", project.Directory);
            }

            case "new_task":
            {
                var project = ResolveProject(ws, job.Arg(0));
                if (project == null)
                    return OperatorResult.Fail($"No project matches \"{job.Arg(0)}\".");
                var task = new TaskItem
                {
                    Id = project.MintTaskId(),
                    Name = job.Arg(1),
                    Prompt = job.Arg(2),
                    Order = project.Tasks.Count,
                };
                project.Tasks.Add(task);
                touched.Add(project);
                return OperatorResult.Pass($"Created {task.Id}", task.Id);
            }

            case "new_subtask":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                task.Subtasks.Add(new Subtask { Text = job.Arg(1) });
                touched.Add(project!);
                return OperatorResult.Pass($"{task.Id} +subtask");
            }

            case "subtask_done":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                if (!int.TryParse(job.Arg(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var si)
                    || si < 0 || si >= task.Subtasks.Count)
                    return OperatorResult.Fail($"{task.Id} has no subtask #{job.Arg(1)}.");
                task.Subtasks[si].Done = job.Arg(2) is "true" or "1";
                touched.Add(project!);
                return OperatorResult.Pass($"{task.Id} subtask #{si} = {task.Subtasks[si].Done}");
            }

            case "delete":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                project!.Tasks.Remove(task);
                touched.Add(project);
                return OperatorResult.Pass($"Deleted {job.Arg(0)}");
            }

            case "archive":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                task.InProgress = false;
                task.LockKey = "";
                task.Done = true;
                task.Archived = true;   // ProjectStore.Save moves it into archive.xml
                touched.Add(project!);
                return OperatorResult.Pass($"Archived {task.Id}");
            }

            case "agent_lock":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                var key = job.Arg(1).Trim();
                if (key.Length == 0)
                    return OperatorResult.Fail("A lock key is required.");
                if (task.LockKey.Length > 0 && task.LockKey != key)
                    return OperatorResult.Fail($"{task.Id} is already locked by another agent.");
                task.LockKey = key;
                task.InProgress = true;
                touched.Add(project!);
                return OperatorResult.Pass($"{task.Id} locked");
            }

            case "agent_unlock":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                var key = job.Arg(1).Trim();
                if (task.LockKey.Length > 0 && task.LockKey != key)
                    return OperatorResult.Fail($"{task.Id}'s lock key does not match.");
                task.LockKey = "";
                touched.Add(project!);
                return OperatorResult.Pass($"{task.Id} unlocked");
            }

            case "move":
            {
                var (project, task) = ResolveTask(ws, job.Arg(0));
                if (task == null)
                    return OperatorResult.Fail($"No task \"{job.Arg(0)}\".");
                if (!int.TryParse(job.Arg(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    return OperatorResult.Fail($"\"{job.Arg(1)}\" is not an index.");
                var old = project!.Tasks.IndexOf(task);
                project.Tasks.RemoveAt(old);
                var maxIndex = task.Archived ? project.Tasks.Count : project.Tasks.Count(t => !t.Archived);
                index = Math.Clamp(index, 0, maxIndex);
                project.Tasks.Insert(index, task);
                touched.Add(project);
                return OperatorResult.Pass($"Moved {task.Id} to {index}");
            }

            case "upsert":
            {
                var project = ResolveProject(ws, job.Arg(0));
                if (project == null)
                    return OperatorResult.Fail($"No project matches \"{job.Arg(0)}\".");
                var incoming = DeserializeTask(job.Payload);
                var existing = project.Tasks.FirstOrDefault(t =>
                    string.Equals(t.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    incoming.Order = existing.Order;
                    existing.CopyFrom(incoming);
                }
                else
                {
                    incoming.Order = project.Tasks.Count;
                    project.Tasks.Add(incoming);
                }
                touched.Add(project);
                return OperatorResult.Pass($"Upserted {incoming.Id}");
            }

            default:
                return OperatorResult.Fail($"Unknown command \"{job.Command}\".");
        }
    }

    // ---- resolve -------------------------------------------------------

    private static Project? ResolveProject(Workspace ws, string reference)
    {
        reference = reference.Trim();
        return ws.Projects.FirstOrDefault(p =>
                   string.Equals(p.Name, reference, StringComparison.OrdinalIgnoreCase))
            ?? ws.Projects.FirstOrDefault(p =>
                   string.Equals(Path.GetFullPath(p.Directory).TrimEnd('\\', '/'),
                       SafeFullPath(reference), StringComparison.OrdinalIgnoreCase))
            ?? ws.Projects.FirstOrDefault(p =>
                   string.Equals(p.IdPrefix, reference, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeName(string name)
    {
        var clean = new string(name
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '-')
            .ToArray()).Trim().Trim('.');
        return clean.Length > 0 ? clean : "project";
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    private static (Project? project, TaskItem? task) ResolveTask(Workspace ws, string taskId)
    {
        taskId = taskId.Trim();
        foreach (var p in ws.Projects)
        {
            var t = p.Tasks.FirstOrDefault(x =>
                string.Equals(x.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (t != null)
                return (p, t);
        }
        return (null, null);
    }

    // ---- queue file --------------------------------------------------

    private static List<OperatorJob> LoadQueue()
    {
        if (!File.Exists(QueuePath))
            return new List<OperatorJob>();
        try
        {
            var doc = XDocument.Load(QueuePath);
            return doc.Root?.Elements("job").Select(OperatorJob.FromElement).ToList()
                   ?? new List<OperatorJob>();
        }
        catch
        {
            // A corrupt queue must not wedge everything; start clean.
            return new List<OperatorJob>();
        }
    }

    private static void SaveQueue(List<OperatorJob> queue)
    {
        Directory.CreateDirectory(QueueDirectory);
        var root = new XElement("queue");
        foreach (var job in queue)
            root.Add(job.ToElement());
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(QueuePath);
    }

    // ---- task <-> xml (for the upsert payload) ----------------------

    private static string SerializeTask(TaskItem task)
    {
        var project = new Project { Name = "x", NextIndex = 1 };
        project.Tasks.Add(task);
        // Reuse the real serializer, then lift out the single <task> element.
        var el = TaskXmlSerializer.ToElement(project).Element("task");
        return el?.ToString() ?? "<task/>";
    }

    private static TaskItem DeserializeTask(string xml)
    {
        var wrapped = $"<tasks project=\"x\" nextIndex=\"1\">{xml}</tasks>";
        var doc = TaskXmlSerializer.Deserialize(wrapped);
        return doc.Tasks.Count > 0 ? doc.Tasks[0] : new TaskItem();
    }
}
