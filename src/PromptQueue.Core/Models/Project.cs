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
    private string _loadError = "";
    private bool _isLocal;
    private string _lastTaskBranch = "main";

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

    /// <summary>
    /// Set when the project's <c>tasks.xml</c> could not be parsed (even after
    /// the lenient repair pass). While this is non-empty the task list in memory
    /// is empty and MUST NOT be written back over the file on disk, or the
    /// unreadable-but-present task data would be lost.
    /// </summary>
    public string LoadError
    {
        get => _loadError;
        set { if (Set(ref _loadError, value)) Raise(nameof(HasLoadError)); }
    }

    public bool HasLoadError => !string.IsNullOrEmpty(_loadError);

    /// <summary>
    /// A "local project" (ZP-70): a personal to-do list stored under the
    /// workspace's <c>local/</c> folder. It has no meaningful source path and AI
    /// agents never work on it (the deploy actions are disabled for it).
    /// </summary>
    public bool IsLocal
    {
        get => _isLocal;
        set => Set(ref _isLocal, value);
    }

    public ObservableCollection<TaskItem> Tasks { get; } = new();

    /// <summary>
    /// User-controlled processing order for branch groups. It is persisted in
    /// tasks.xml and also drives the physical order of active task elements so
    /// command-line agents encounter branches in the same order as the UI.
    /// </summary>
    public ObservableCollection<string> BranchOrder { get; } = new();

    /// <summary>The branch preselected when the next task or note is created.</summary>
    public string LastTaskBranch
    {
        get => string.IsNullOrWhiteSpace(_lastTaskBranch) ? "main" : _lastTaskBranch;
        set => Set(ref _lastTaskBranch, NormalizeBranchPath(value));
    }

    /// <summary>Adds missing task branches, removes duplicates, and guarantees a main option.</summary>
    public void EnsureBranchOrder()
    {
        var requested = BranchOrder
            .Select(branch => string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim())
            .Append(LastTaskBranch)
            .Concat(Tasks.Where(task => !task.Done && !task.Archived && !task.FinishedToday)
                .Select(task => task.BranchDisplay))
            .ToList();

        var discovered = new List<string>();
        foreach (var requestedBranch in requested.Append("main"))
        {
            var branch = NormalizeBranchPath(requestedBranch);
            var lineage = new Stack<string>();
            for (var current = branch; current != null; current = ParentBranch(current))
                lineage.Push(current);
            while (lineage.Count > 0)
            {
                var member = lineage.Pop();
                if (!discovered.Contains(member, StringComparer.OrdinalIgnoreCase))
                    discovered.Add(member);
            }
        }

        var normalized = new List<string>();
        void AppendChildren(string? parent)
        {
            foreach (var branch in discovered.Where(candidate =>
                         string.Equals(ParentBranch(candidate), parent, StringComparison.OrdinalIgnoreCase)))
            {
                normalized.Add(branch);
                AppendChildren(branch);
            }
        }
        AppendChildren(null);

        if (BranchOrder.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            return;
        BranchOrder.Clear();
        foreach (var branch in normalized)
            BranchOrder.Add(branch);
    }

    /// <summary>
    /// Removes completed non-main branches from the active registry once their
    /// merge task is done and no unfinished work remains. Task history retains
    /// its original Branch value.
    /// </summary>
    public IReadOnlyList<string> CloseCompletedBranches()
    {
        var closed = new List<string>();
        bool changed;
        do
        {
            changed = false;
            var candidates = Tasks
                .Where(task => task.Merge && task.Done &&
                               !string.Equals(task.BranchDisplay, "main", StringComparison.OrdinalIgnoreCase))
                .Select(task => NormalizeBranchPath(task.BranchDisplay))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(BranchDepth)
                .ToList();
            foreach (var branch in candidates)
            {
                var branchIndex = BranchOrder.ToList().FindIndex(item =>
                    string.Equals(item, branch, StringComparison.OrdinalIgnoreCase));
                if (branchIndex < 0 ||
                    Tasks.Any(task => !task.Done && !task.Archived && !task.FinishedToday &&
                        string.Equals(task.BranchDisplay, branch, StringComparison.OrdinalIgnoreCase)) ||
                    BranchOrder.Any(candidate => IsDescendantBranch(candidate, branch)))
                    continue;
                BranchOrder.RemoveAt(branchIndex);
                closed.Add(branch);
                changed = true;
            }
        } while (changed);

        if (closed.Any(branch => string.Equals(branch, LastTaskBranch, StringComparison.OrdinalIgnoreCase)))
        {
            var fallback = ParentBranch(LastTaskBranch);
            LastTaskBranch = fallback != null && BranchOrder.Contains(fallback, StringComparer.OrdinalIgnoreCase)
                ? fallback
                : BranchOrder.FirstOrDefault() ?? "main";
        }
        return closed;
    }

    /// <summary>Unfinished actionable work that must finish before this merge task can run.</summary>
    public IReadOnlyList<TaskItem> MergeBlockers(TaskItem mergeTask)
        => Tasks.Where(task =>
                !ReferenceEquals(task, mergeTask) &&
                !string.Equals(task.Id, mergeTask.Id, StringComparison.OrdinalIgnoreCase) &&
                !task.IsNote && !task.StopExecution && !task.Done && !task.Archived &&
                string.Equals(task.BranchDisplay, mergeTask.BranchDisplay, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Open descendant branches that must close before this branch can merge.</summary>
    public IReadOnlyList<string> MergeBlockingBranches(TaskItem mergeTask)
        => BranchOrder.Where(branch => IsDescendantBranch(branch, mergeTask.BranchDisplay)).ToList();

    public IReadOnlyList<string> MergeBlockerLabels(TaskItem mergeTask)
        => MergeBlockers(mergeTask).Select(task => task.Id)
            .Concat(MergeBlockingBranches(mergeTask).Select(branch => $"branch:{branch}"))
            .ToList();

    public int MergeBlockerCount(TaskItem mergeTask) => MergeBlockerLabels(mergeTask).Count;

    public static string NormalizeBranchPath(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return "main";
        var segments = branch.Replace('\\', '/').Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "main" : string.Join('/', segments);
    }

    public static string? ParentBranch(string? branch)
    {
        var normalized = NormalizeBranchPath(branch);
        var split = normalized.LastIndexOf('/');
        return split < 0 ? null : normalized[..split];
    }

    public static bool IsDescendantBranch(string? candidate, string? ancestor)
    {
        var child = NormalizeBranchPath(candidate);
        var parent = NormalizeBranchPath(ancestor);
        return child.Length > parent.Length && child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static int BranchDepth(string? branch) => NormalizeBranchPath(branch).Count(ch => ch == '/');

    /// <summary>The concrete source-control name for a logical slash-delimited branch path.</summary>
    public static string ConcreteBranchName(string? branch) => NormalizeBranchPath(branch).Replace('/', '_');

    /// <summary>Moves a branch and its descendants among siblings without breaking the hierarchy.</summary>
    public bool MoveBranch(string branch, int offset)
    {
        EnsureBranchOrder();
        branch = NormalizeBranchPath(branch);
        var parent = ParentBranch(branch);
        var siblings = BranchOrder.Where(candidate =>
                string.Equals(ParentBranch(candidate), parent, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var position = siblings.FindIndex(candidate => string.Equals(candidate, branch, StringComparison.OrdinalIgnoreCase));
        var targetPosition = position + (offset < 0 ? -1 : 1);
        if (position < 0 || targetPosition < 0 || targetPosition >= siblings.Count)
            return false;

        var target = siblings[targetPosition];
        var sourceGroup = BranchOrder.Where(candidate =>
                string.Equals(candidate, branch, StringComparison.OrdinalIgnoreCase) || IsDescendantBranch(candidate, branch))
            .ToList();
        foreach (var member in sourceGroup)
            BranchOrder.Remove(member);

        var insertAt = BranchOrder.ToList().FindIndex(candidate =>
            string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase));
        if (offset > 0)
        {
            insertAt++;
            while (insertAt < BranchOrder.Count && IsDescendantBranch(BranchOrder[insertAt], target))
                insertAt++;
        }
        for (var i = 0; i < sourceGroup.Count; i++)
            BranchOrder.Insert(insertAt + i, sourceGroup[i]);
        return true;
    }

    public int BranchRank(string? branch)
    {
        EnsureBranchOrder();
        var normalized = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        for (var i = 0; i < BranchOrder.Count; i++)
            if (string.Equals(BranchOrder[i], normalized, StringComparison.OrdinalIgnoreCase))
                return i;
        return BranchOrder.Count;
    }

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
