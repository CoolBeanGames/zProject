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

        var tasksPath = Path.Combine(directory, TaskXmlSerializer.FileName);
        if (File.Exists(tasksPath))
        {
            try
            {
                var doc = TaskXmlSerializer.Deserialize(File.ReadAllText(tasksPath));
                project.NextIndex = doc.NextIndex;
                foreach (var t in doc.Tasks)
                    project.Tasks.Add(t);
            }
            catch (TaskXmlFormatException ex)
            {
                // One project's broken tasks.xml must not stop the whole app
                // from opening. Leave the task list empty and flag the project;
                // ProjectStore.Save then refuses to overwrite the file so the
                // real data is preserved for the user to fix by hand.
                project.LoadError =
                    $"{TaskXmlSerializer.FileName} could not be read: {ex.Message}";
            }
        }

        project.LocalDesign = ReadTextOrEmpty(Path.Combine(directory, DesignFile));
        project.LocalInstructions = ReadTextOrEmpty(Path.Combine(directory, InstructionsFile));
        project.LocalPrompt = ReadTextOrEmpty(Path.Combine(directory, PromptFile));

        return project;
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
        Normalize(project);

        // Serialise tasks.xml writes against the operator / other processes (ZP-65)
        // so an agent's write and the app's write never interleave.
        using (new CrossProcessLock(OperatorEngine.MutexName))
        {
            File.WriteAllText(
                Path.Combine(project.Directory, TaskXmlSerializer.FileName),
                TaskXmlSerializer.Serialize(project));
        }

        WriteOrDelete(Path.Combine(project.Directory, DesignFile), project.LocalDesign);
        WriteOrDelete(Path.Combine(project.Directory, InstructionsFile), project.LocalInstructions);
        WriteOrDelete(Path.Combine(project.Directory, PromptFile), project.LocalPrompt);
    }

    /// <summary>Re-reads tasks.xml into an existing project, replacing its task list.</summary>
    public static void ReloadTasks(Project project)
    {
        var tasksPath = Path.Combine(project.Directory, TaskXmlSerializer.FileName);
        if (!File.Exists(tasksPath))
            return;

        TaskXmlSerializer.Document doc;
        try
        {
            doc = TaskXmlSerializer.Deserialize(File.ReadAllText(tasksPath));
        }
        catch (TaskXmlFormatException ex)
        {
            // Keep the in-memory list as-is; just flag the project so the UI can
            // warn and Save() stops overwriting the broken file.
            project.LoadError =
                $"{TaskXmlSerializer.FileName} could not be read: {ex.Message}";
            return;
        }

        project.LoadError = "";
        project.Tasks.Clear();
        foreach (var t in doc.Tasks)
            project.Tasks.Add(t);
        project.NextIndex = Math.Max(project.NextIndex, doc.NextIndex);
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
