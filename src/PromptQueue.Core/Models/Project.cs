using System.Collections.ObjectModel;

namespace PromptQueue.Core.Models;

/// <summary>
/// A managed project. Its <see cref="Directory"/> is the root under which all
/// of the project's files live (tasks.xml plus the local design / instructions
/// / prompt overrides). The project's <see cref="Name"/> mirrors that
/// directory's name.
/// </summary>
public sealed class Project : Observable
{
    private string _name = "";
    private string _directory = "";
    private string _localDesign = "";
    private string _localInstructions = "";
    private string _localPrompt = "";
    private int _nextIndex = 1;

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Raise(nameof(IdPrefix)); }
    }

    /// <summary>Absolute path to the project's root directory.</summary>
    public string Directory
    {
        get => _directory;
        set => Set(ref _directory, value);
    }

    public string LocalDesign
    {
        get => _localDesign;
        set => Set(ref _localDesign, value);
    }

    public string LocalInstructions
    {
        get => _localInstructions;
        set => Set(ref _localInstructions, value);
    }

    public string LocalPrompt
    {
        get => _localPrompt;
        set => Set(ref _localPrompt, value);
    }

    /// <summary>Monotonic counter used to mint the next task id.</summary>
    public int NextIndex
    {
        get => _nextIndex;
        set => Set(ref _nextIndex, value);
    }

    public ObservableCollection<TaskItem> Tasks { get; } = new();

    /// <summary>The id prefix derived from the project name, e.g. "MCA".</summary>
    public string IdPrefix => IdGenerator.PrefixFor(Name);

    /// <summary>Mints the next task id and advances the counter.</summary>
    public string MintTaskId()
    {
        var id = $"{IdPrefix}-{NextIndex}";
        NextIndex++;
        return id;
    }
}
