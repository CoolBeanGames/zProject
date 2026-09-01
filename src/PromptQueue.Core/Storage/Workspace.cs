using System.Collections.ObjectModel;
using System.Xml.Linq;
using PromptQueue.Core.Models;

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

    public const string IndexFile = "workspace.xml";
    public const string GlobalDesignFile = "global.design.txt";
    public const string GlobalInstructionsFile = "global.instructions.txt";
    public const string GlobalPromptFile = "global.prompt.txt";

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
                ws.Projects.Add(ProjectStore.Load(dir));
            }
        }

        return ws;
    }

    /// <summary>Persists the project index and the global text files.</summary>
    public void Save()
    {
        Directory.CreateDirectory(RootDirectory);

        var root = new XElement("workspace");
        foreach (var p in Projects)
            root.Add(new XElement("project", new XAttribute("directory", p.Directory)));
        new XDocument(new XDeclaration("1.0", "utf-8", null), root)
            .Save(Path.Combine(RootDirectory, IndexFile));

        File.WriteAllText(Path.Combine(RootDirectory, GlobalDesignFile), GlobalDesign);
        File.WriteAllText(Path.Combine(RootDirectory, GlobalInstructionsFile), GlobalInstructions);
        File.WriteAllText(Path.Combine(RootDirectory, GlobalPromptFile), GlobalPrompt);
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
        Projects.Add(project);
        Save();
        ProjectStore.Save(project);
        return project;
    }

    private static string ReadTextOrEmpty(string path)
        => File.Exists(path) ? File.ReadAllText(path) : "";
}
