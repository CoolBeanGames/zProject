using PromptQueue.Core.Models;
using PromptQueue.Core.Operator;
using PromptQueue.Core.Serialization;

namespace PromptQueue.Core.Storage;

/// <summary>
/// Persists a single project inside its own root directory. Everything a
/// project owns lives there as plain, human-readable files:
/// <list type="bullet">
///   <item><c>tasks.xml</c> — the prompt queue</item>
///   <item><c>design.txt</c> — local design overrides</item>
///   <item><c>instructions.txt</c> — local instruction overrides</item>
///   <item><c>prompt.txt</c> — local base prompt override</item>
/// </list>
/// </summary>
public static class ProjectStore
{
    public const string DesignFile = "design.txt";
    public const string InstructionsFile = "instructions.txt";
    public const string PromptFile = "prompt.txt";

    /// <summary>
    /// Loads the project rooted at <paramref name="directory"/>, creating an
    /// empty in-memory project if no files exist yet.
    /// </summary>
    public static Project Load(string directory)
    {
        directory = Path.GetFullPath(directory);
        var project = new Project
        {
            Directory = directory,
            Name = new DirectoryInfo(directory).Name,
        };

        var legacyInlineArchive = ReadTasksInto(project);

        project.LocalDesign = ReadTextOrEmpty(Path.Combine(directory, DesignFile));
        project.LocalInstructions = ReadTextOrEmpty(Path.Combine(directory, InstructionsFile));
        project.LocalPrompt = ReadTextOrEmpty(Path.Combine(directory, PromptFile));

        // ZP-71: an older tasks.xml keeps archived tasks inline. Split them out
        // to archive.xml now so tasks.xml stays small.
        if (legacyInlineArchive && !project.HasLoadError)
            Save(project);

        return project;
    }

    /// <summary>
    /// Reads <c>tasks.xml</c> + <c>archive.xml</c> into <paramref name="project"/>.Tasks
    /// (ZP-71). Returns true when tasks.xml still held archived tasks inline (so the
    /// caller can migrate). On a parse failure the project is flagged and its list
    /// left as it was, so Save won't overwrite the real data.
    /// </summary>
    private static bool ReadTasksInto(Project project)
    {
        var tasksPath = Path.Combine(project.Directory, TaskXmlSerializer.FileName);
        var archivePath = Path.Combine(project.Directory, TaskXmlSerializer.ArchiveFileName);

        bool legacyInline = false;
        var loaded = new List<TaskItem>();
        int nextIndex = project.NextIndex;

        if (File.Exists(tasksPath))
        {
            try
            {
                var doc = TaskXmlSerializer.Deserialize(File.ReadAllText(tasksPath));
                nextIndex = doc.NextIndex;
                loaded.AddRange(doc.Tasks);
                legacyInline = doc.Tasks.Any(t => t.Archived);
            }
            catch (TaskXmlFormatException ex)
            {
                project.LoadError =
                    $"{TaskXmlSerializer.FileName} could not be read: {ex.Message}";
                return false;
            }
        }

        if (File.Exists(archivePath))
        {
            try
            {
                var adoc = TaskXmlSerializer.Deserialize(File.ReadAllText(archivePath));
                nextIndex = Math.Max(nextIndex, adoc.NextIndex);
                foreach (var t in adoc.Tasks)
                {
                    t.Archived = true;
                    loaded.Add(t);
                }
            }
            catch (TaskXmlFormatException ex)
            {
                project.LoadError =
                    $"{TaskXmlSerializer.ArchiveFileName} could not be read: {ex.Message}";
                return false;
            }
        }

        project.LoadError = "";
        project.NextIndex = Math.Max(project.NextIndex, nextIndex);
        project.Tasks.Clear();
        foreach (var t in loaded)
            project.Tasks.Add(t);
        return legacyInline;
    }

    /// <summary>Writes every file the project owns to its directory.</summary>
    public static void Save(Project project)
    {
        System.IO.Directory.CreateDirectory(project.Directory);

        // The on-disk tasks.xml is unreadable and was NOT loaded into memory;
        // writing our empty list back would destroy it. Persist the text files
        // (they loaded fine) but leave tasks.xml for the user to repair.
        if (project.HasLoadError)
        {
            WriteOrDelete(Path.Combine(project.Directory, DesignFile), project.LocalDesign);
            WriteOrDelete(Path.Combine(project.Directory, InstructionsFile), project.LocalInstructions);
            WriteOrDelete(Path.Combine(project.Directory, PromptFile), project.LocalPrompt);
            return;
        }

        AutoArchiveCompleted(project);

        // ZP-71: active tasks -> tasks.xml, archived tasks -> archive.xml, so
        // tasks.xml stays small. Order is renumbered within each file.
        var active = project.Tasks.Where(t => !t.Archived).ToList();
        var archived = project.Tasks.Where(t => t.Archived).ToList();
        for (int i = 0; i < active.Count; i++) active[i].Order = i;
        for (int i = 0; i < archived.Count; i++) archived[i].Order = i;

        var tasksPath = Path.Combine(project.Directory, TaskXmlSerializer.FileName);
        var archivePath = Path.Combine(project.Directory, TaskXmlSerializer.ArchiveFileName);

        // Serialise these writes against the operator / other processes (ZP-65)
        // so an agent's write and the app's write never interleave.
        using (new CrossProcessLock(OperatorEngine.MutexName))
        {
            File.WriteAllText(tasksPath, TaskXmlSerializer.Serialize(project, active));
            if (archived.Count > 0)
                File.WriteAllText(archivePath, TaskXmlSerializer.Serialize(project, archived));
            else if (File.Exists(archivePath))
                File.Delete(archivePath);
        }

        WriteOrDelete(Path.Combine(project.Directory, DesignFile), project.LocalDesign);
        WriteOrDelete(Path.Combine(project.Directory, InstructionsFile), project.LocalInstructions);
        WriteOrDelete(Path.Combine(project.Directory, PromptFile), project.LocalPrompt);
    }

    /// <summary>Re-reads tasks.xml + archive.xml into an existing project, replacing its task list.</summary>
    public static void ReloadTasks(Project project)
    {
        var tasksPath = Path.Combine(project.Directory, TaskXmlSerializer.FileName);
        var archivePath = Path.Combine(project.Directory, TaskXmlSerializer.ArchiveFileName);
        if (!File.Exists(tasksPath) && !File.Exists(archivePath))
            return;

        // On a parse failure ReadTasksInto flags the project and returns without
        // touching the in-memory list, so Save() won't clobber the real file.
        var legacyInline = ReadTasksInto(project);
        if (legacyInline && !project.HasLoadError)
            Save(project);
    }

    /// <summary>Re-reads everything a project owns (tasks + local text files) from disk.</summary>
    public static void ReloadInto(Project project)
    {
        ReloadTasks(project);
        project.LocalDesign = ReadTextOrEmpty(Path.Combine(project.Directory, DesignFile));
        project.LocalInstructions = ReadTextOrEmpty(Path.Combine(project.Directory, InstructionsFile));
        project.LocalPrompt = ReadTextOrEmpty(Path.Combine(project.Directory, PromptFile));
    }

    /// <summary>
    /// Archiving completed tasks is automatic on every save (ZP-47): any task
    /// that is <see cref="TaskItem.Done"/> and not yet <see cref="TaskItem.Archived"/>
    /// is moved to the archive, after which an agent ignores it entirely.
    /// Returns true when at least one task was archived.
    /// </summary>
    public static bool AutoArchiveCompleted(Project project)
    {
        bool changed = false;
        foreach (var t in project.Tasks)
        {
            if (t.Done && !t.Archived)
            {
                t.Archived = true;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Sets each task's <see cref="TaskItem.Order"/> to its physical position in
    /// the collection (0..n-1). The collection order is authoritative — a drag or
    /// a section re-sort changes the collection, and Order must follow it, not the
    /// other way around.
    /// </summary>
    public static void Normalize(Project project)
    {
        for (int i = 0; i < project.Tasks.Count; i++)
            project.Tasks[i].Order = i;
    }

    private static string ReadTextOrEmpty(string path)
        => File.Exists(path) ? File.ReadAllText(path) : "";

    private static void WriteOrDelete(string path, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        File.WriteAllText(path, content);
    }
}
