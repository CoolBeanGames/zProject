using System.Text;
using PromptQueue.Core.Operator;

namespace PromptQueue.Operator;

/// <summary>
/// <c>zProject_operator.exe</c> — the command-line face of the <see cref="OperatorEngine"/>
/// (ZP-65). The app, the AI agents and the web server all route their task
/// changes through this so writes to a project's tasks.xml are serialised while
/// an agent is running. It does its work and exits; it never opens a window of
/// its own (callers start it hidden).
///
/// <para>Usage:</para>
/// <code>
///   operator read     &lt;project&gt;                  print a project's tasks.xml
///   operator list                                  list known projects
///   operator sync     &lt;task_id&gt; &lt;field&gt; &lt;value&gt;   override one field on a task
///   operator new_task &lt;project&gt; &lt;name&gt; [prompt]  create a task (prints the new id)
///   operator new_subtask &lt;task_id&gt; &lt;text...&gt;      add a checklist line to a task
///   operator delete   &lt;task_id&gt;                   remove a task
///   operator move     &lt;task_id&gt; &lt;index&gt;           move a task to a 0-based position
/// </code>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            var result = Dispatch(command, rest);
            if (!string.IsNullOrEmpty(result.Output))
                Console.WriteLine(result.Output);
            if (!result.Ok)
            {
                Console.Error.WriteLine($"operator: {result.Message}");
                return 1;
            }
            Console.WriteLine($"operator: {result.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"operator: {ex.Message}");
            return 1;
        }
    }

    private static OperatorResult Dispatch(string command, string[] a) => command switch
    {
        "read" when a.Length >= 1 => OperatorEngine.Read(a[0]),
        "instructions" when a.Length >= 1 => OperatorEngine.Instructions(a[0]),
        "get_archive" when a.Length >= 1 => OperatorEngine.GetArchive(a[0]),
        "get_finished_today" when a.Length >= 1 => OperatorEngine.GetFinishedToday(a[0]),
        "get_tag" when a.Length >= 2 => OperatorEngine.GetTag(a[0], a[1]),
        "list" => OperatorEngine.List(),
        "sync" when a.Length >= 3 => OperatorEngine.Sync(a[0], a[1], string.Join(' ', a.Skip(2))),
        "new_task" when a.Length >= 2 => OperatorEngine.NewTask(a[0], a[1], a.Length > 2 ? string.Join(' ', a.Skip(2)) : ""),
        "new_note" when a.Length >= 2 => OperatorEngine.NewNote(a[0], a[1], a.Length > 2 ? string.Join(' ', a.Skip(2)) : ""),
        "new_stop" when a.Length >= 1 => OperatorEngine.NewStop(a[0]),
        "new_project" when a.Length >= 1 => OperatorEngine.NewProject(a[0], a.Length > 1 ? a[1] : null),
        "new_local_project" when a.Length >= 1 => OperatorEngine.NewLocalProject(a[0]),
        "sync_many" when a.Length >= 3 && a.Length % 2 == 1 => OperatorEngine.SyncMany(a[0], Pairs(a.Skip(1))),
        "sync_many" => OperatorResult.Fail("sync_many needs complete <field> <value> pairs"),
        "new_subtask" when a.Length >= 2 => OperatorEngine.NewSubtask(a[0], string.Join(' ', a.Skip(1))),
        "subtask_done" when a.Length >= 3 && int.TryParse(a[1], out var sdi) && TryBool(a[2], out var sdv) =>
            OperatorEngine.SetSubtaskDone(a[0], sdi, sdv),
        "subtask_text" when a.Length >= 3 && int.TryParse(a[1], out var sti) => OperatorEngine.SetSubtaskText(a[0], sti, string.Join(' ', a.Skip(2))),
        "subtask_delete" when a.Length >= 2 && int.TryParse(a[1], out var sxi) => OperatorEngine.DeleteSubtask(a[0], sxi),
        "new_approval" when a.Length >= 2 => OperatorEngine.NewApproval(a[0], string.Join(' ', a.Skip(1))),
        "approval_text" when a.Length >= 3 && int.TryParse(a[1], out var ati) => OperatorEngine.SetApprovalText(a[0], ati, string.Join(' ', a.Skip(2))),
        "approval_delete" when a.Length >= 2 && int.TryParse(a[1], out var adi) => OperatorEngine.DeleteApproval(a[0], adi),
        "delete" when a.Length >= 1 => OperatorEngine.Delete(a[0]),
        "archive" when a.Length >= 1 => OperatorEngine.Archive(a[0]),
        "agent_lock" when a.Length >= 2 => OperatorEngine.AgentLock(a[0], a[1]),
        "agent_unlock" when a.Length >= 2 => OperatorEngine.AgentUnlock(a[0], a[1]),
        "move" when a.Length >= 2 && int.TryParse(a[1], out var i) => OperatorEngine.Move(a[0], i),
        "move" => OperatorResult.Fail("move needs: <task_id> <index>"),
        "move_relative" when a.Length >= 3 &&
            (a[2].Equals("above", StringComparison.OrdinalIgnoreCase) || a[2].Equals("below", StringComparison.OrdinalIgnoreCase)) =>
            OperatorEngine.MoveRelative(a[0], a[1], a[2].Equals("above", StringComparison.OrdinalIgnoreCase)),
        "move_relative" => OperatorResult.Fail("move_relative needs: <task_id> <target_id> <above|below>"),
        "block_task" when a.Length >= 2 => OperatorEngine.BlockTask(a[0], a[1]),
        "block_task" => OperatorResult.Fail("block_task needs: <task_id> <blocker_id>"),
        "branch_lock" when a.Length >= 3 && TryBool(a[2], out var blv) => OperatorEngine.SetBranchLocked(a[0], a[1], blv),
        "branch_lock" => OperatorResult.Fail("branch_lock needs: <project> <branch> <true|false>"),
        "branch_move" when a.Length >= 3 &&
            (a[2].Equals("up", StringComparison.OrdinalIgnoreCase) || a[2].Equals("down", StringComparison.OrdinalIgnoreCase)) =>
            OperatorEngine.MoveBranch(a[0], a[1], a[2].Equals("up", StringComparison.OrdinalIgnoreCase) ? -1 : 1),
        "branch_move" => OperatorResult.Fail("branch_move needs: <project> <branch> <up|down>"),
        "branch_default" when a.Length >= 2 => OperatorEngine.SetDefaultBranch(a[0], a[1]),
        "branch_default" => OperatorResult.Fail("branch_default needs: <project> <branch>"),
        "branch_add" when a.Length >= 2 => OperatorEngine.AddBranch(a[0], string.Join(' ', a.Skip(1))),
        "branch_add" => OperatorResult.Fail("branch_add needs: <project> <branch>"),
        _ => OperatorResult.Fail($"bad or incomplete command. \n{Usage}"),
    };

    private static bool TryBool(string value, out bool parsed)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            parsed = true;
            return true;
        }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            parsed = false;
            return true;
        }
        parsed = false;
        return false;
    }

    private static IEnumerable<KeyValuePair<string, string>> Pairs(IEnumerable<string> items)
    {
        var list = items.ToList();
        for (int i = 0; i + 1 < list.Count; i += 2)
            yield return new KeyValuePair<string, string>(list[i], list[i + 1]);
    }

    private const string Usage = """
        zProject operator

          operator read       <project>
          operator instructions <project>
          operator get_archive <project>
          operator get_finished_today <project>
          operator get_tag    <task_id> <field>
          operator list
          operator sync       <task_id> <field> <value>
          operator archive    <task_id>
          operator agent_lock <task_id> <key>
          operator agent_unlock <task_id> <key>
          operator new_task   <project> <name> [prompt]
          operator new_note   <project> <name> [note]
          operator new_stop   <project>
          operator new_project <name> [directory]
          operator new_local_project <name>
          operator new_subtask <task_id> <text...>
          operator subtask_done <task_id> <index> <true|false>
          operator subtask_text <task_id> <index> <text...>
          operator subtask_delete <task_id> <index>
          operator new_approval <task_id> <text...>
          operator approval_text <task_id> <index> <text...>
          operator approval_delete <task_id> <index>
          operator sync_many  <task_id> <field> <value> [<field> <value> ...]
          operator delete     <task_id>
          operator move       <task_id> <index>
          operator move_relative <task_id> <target_id> <above|below>
          operator block_task <task_id> <blocker_id>
          operator branch_lock <project> <branch> <true|false>
          operator branch_move <project> <branch> <up|down>
          operator branch_default <project> <branch>
          operator branch_add <project> <branch>

        <project> is a project name, id prefix, or directory path.
        <field>   is a tasks.xml element name, e.g. done, inProgress, locked,
                  bug, notes, filesChanged, blockedBy, dueDate.
        """;
}
