using System.Collections.ObjectModel;
using System.Xml.Linq;
using PromptQueue.Core.Models;
using PromptQueue.Core.Serialization;

namespace PromptQueue.Core.Storage;

/// <summary>
/// The application-level state: the set of known projects plus the global
/// design / instructions / prompt defaults that individual projects can
/// override. Persisted under a single folder so the whole workspace stays
/// portable and human-inspectable.
/// </summary>
public sealed class Workspace : Observable
{
    private string _globalDesign = "";
    private string _globalInstructions = "";
    private string _globalPrompt = "";

    /// <summary>Directory holding workspace.xml and the global text files.</summary>
    public string RootDirectory { get; init; } = DefaultRootDirectory;

    public ObservableCollection<Project> Projects { get; } = new();

    public string GlobalDesign
    {
        get => _globalDesign;
        set => Set(ref _globalDesign, value);
    }

    public string GlobalInstructions
    {
        get => _globalInstructions;
        set => Set(ref _globalInstructions, value);
    }

    public string GlobalPrompt
    {
        get => _globalPrompt;
        set => Set(ref _globalPrompt, value);
    }

    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PromptQueue");

    /// <summary>Folder that holds "local projects" (ZP-70) — personal to-do lists.</summary>
    public string LocalProjectsRoot => Path.Combine(RootDirectory, "local");

    /// <summary>True when <paramref name="directory"/> sits under <see cref="LocalProjectsRoot"/>.</summary>
    public bool IsLocalPath(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd('\\', '/');
            var root = Path.GetFullPath(LocalProjectsRoot).TrimEnd('\\', '/');
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public const string IndexFile = "workspace.xml";
    public const string GlobalDesignFile = "global.design.txt";
    public const string GlobalInstructionsFile = "global.instructions.txt";
    public const string GlobalPromptFile = "global.prompt.txt";

    /// <summary>Consolidated, portable database written next to the executable.</summary>
    public const string DataCfgFile = "data.cfg";

    public static string DataCfgPath => Path.Combine(AppContext.BaseDirectory, DataCfgFile);

    public static Workspace Load(string? rootDirectory = null)
    {
        var root = rootDirectory ?? DefaultRootDirectory;
        var ws = new Workspace { RootDirectory = root };
        Directory.CreateDirectory(root);

        ws.GlobalDesign = ReadTextOrEmpty(Path.Combine(root, GlobalDesignFile));
        ws.GlobalInstructions = ReadTextOrEmpty(Path.Combine(root, GlobalInstructionsFile));
        ws.GlobalPrompt = ReadTextOrEmpty(Path.Combine(root, GlobalPromptFile));

        var indexPath = Path.Combine(root, IndexFile);
        if (File.Exists(indexPath))
        {
            var doc = XDocument.Load(indexPath);
            foreach (var el in doc.Root?.Elements("project") ?? Enumerable.Empty<XElement>())
            {
                var dir = (string?)el.Attribute("directory");
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    continue;
                var project = ProjectStore.Load(dir);
                project.IsLocal = (bool?)el.Attribute("local") ?? ws.IsLocalPath(dir);
                ws.Projects.Add(project);
            }
        }
        else
        {
            // No per-user index yet: fall back to the portable data.cfg if one
            // shipped alongside the executable.
            ws.HydrateFromDataCfg();
        }

        // Keep the portable database current from the moment the app opens.
        ws.SaveDataCfg();

        return ws;
    }

    /// <summary>Persists the project index, the global text files and data.cfg.</summary>
    public void Save()
    {
        Directory.CreateDirectory(RootDirectory);

        var root = new XElement("workspace");
        foreach (var p in Projects)
        {
            var el = new XElement("project", new XAttribute("directory", p.Directory));
            if (p.IsLocal)
                el.Add(new XAttribute("local", true));
            root.Add(el);
        }
        new XDocument(new XDeclaration("1.0", "utf-8", null), root)
            .Save(Path.Combine(RootDirectory, IndexFile));

        File.WriteAllText(Path.Combine(RootDirectory, GlobalDesignFile), GlobalDesign);
        File.WriteAllText(Path.Combine(RootDirectory, GlobalInstructionsFile), GlobalInstructions);
        File.WriteAllText(Path.Combine(RootDirectory, GlobalPromptFile), GlobalPrompt);

        SaveDataCfg();
    }

    /// <summary>
    /// Writes <c>data.cfg</c> to the executable directory: an active database of
    /// every project (name + path + its task queue) plus verbatim copies of the
    /// global design / instructions / prompt.
    /// </summary>
    public void SaveDataCfg()
    {
        var root = new XElement("promptQueueData",
            new XAttribute("savedUtc", DateTime.UtcNow.ToString("o")));

        var projects = new XElement("projects");
        foreach (var p in Projects)
        {
            // A project whose tasks.xml failed to load has an empty in-memory
            // task list; mirroring that into data.cfg could later hydrate it as
            // a real (empty) project. Skip it — the on-disk tasks.xml is intact.
            if (p.HasLoadError)
                continue;

            var pe = new XElement("project",
                new XAttribute("name", p.Name),
                new XAttribute("directory", p.Directory),
                TaskXmlSerializer.ToElement(p));
            if (p.IsLocal)
                pe.Add(new XAttribute("local", true));
            projects.Add(pe);
        }
        root.Add(projects);

        root.Add(new XElement("globalDesign", new XCData(GlobalDesign)));
        root.Add(new XElement("globalInstructions", new XCData(GlobalInstructions)));
        root.Add(new XElement("globalPrompt", new XCData(GlobalPrompt)));

        try
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(DataCfgPath);
        }
        catch (IOException)
        {
            // The executable directory may be read-only (e.g. Program Files);
            // data.cfg is a convenience mirror, so a failure here is non-fatal.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Loads projects and globals from data.cfg when there is no workspace.xml.</summary>
    private void HydrateFromDataCfg()
    {
        if (!File.Exists(DataCfgPath))
            return;

        XDocument doc;
        try
        {
            doc = XDocument.Load(DataCfgPath);
        }
        catch
        {
            return;
        }

        var root = doc.Root;
        if (root == null)
            return;

        GlobalDesign = (string?)root.Element("globalDesign") ?? GlobalDesign;
        GlobalInstructions = (string?)root.Element("globalInstructions") ?? GlobalInstructions;
        GlobalPrompt = (string?)root.Element("globalPrompt") ?? GlobalPrompt;

        foreach (var el in root.Element("projects")?.Elements("project") ?? Enumerable.Empty<XElement>())
        {
            var dir = (string?)el.Attribute("directory");
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;
            if (Projects.Any(p => string.Equals(p.Directory, dir, StringComparison.OrdinalIgnoreCase)))
                continue;
            var hydrated = ProjectStore.Load(dir);
            hydrated.IsLocal = (bool?)el.Attribute("local") ?? IsLocalPath(dir);
            Projects.Add(hydrated);
        }
    }

    /// <summary>Adds the directory as a project (or returns the existing one).</summary>
    public Project AddProject(string directory)
    {
        directory = Path.GetFullPath(directory);
        var existing = Projects.FirstOrDefault(p =>
            string.Equals(p.Directory, directory, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        var project = ProjectStore.Load(directory);
        project.IsLocal = IsLocalPath(directory);
        if (!project.IsLocal)
            SeedLocalsFromGlobals(project);
        Projects.Add(project);
        Save();
        ProjectStore.Save(project);
        return project;
    }

    /// <summary>
    /// Creates a "local project" (ZP-70): a personal to-do list under
    /// <see cref="LocalProjectsRoot"/>/&lt;name&gt;. No source path is needed and
    /// agents never touch it. Returns the existing one if the name is taken.
    /// </summary>
    public Project AddLocalProject(string name)
    {
        var clean = new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '-').ToArray())
            .Trim().Trim('.');
        if (clean.Length == 0)
            clean = "list";

        var dir = Path.Combine(LocalProjectsRoot, clean);
        var project = AddProject(dir);   // IsLocal is set from the path
        return project;
    }

    /// <summary>
    /// A new project with no local design / instructions / prompt of its own
    /// starts from copies of the global versions.
    /// </summary>
    private void SeedLocalsFromGlobals(Project project)
    {
        if (string.IsNullOrEmpty(project.LocalDesign) && !string.IsNullOrEmpty(GlobalDesign))
            project.LocalDesign = GlobalDesign;
        if (string.IsNullOrEmpty(project.LocalInstructions) && !string.IsNullOrEmpty(GlobalInstructions))
            project.LocalInstructions = GlobalInstructions;
        if (string.IsNullOrEmpty(project.LocalPrompt) && !string.IsNullOrEmpty(GlobalPrompt))
            project.LocalPrompt = GlobalPrompt;
    }

    private static string ReadTextOrEmpty(string path)
        => File.Exists(path) ? File.ReadAllText(path) : "";
}
