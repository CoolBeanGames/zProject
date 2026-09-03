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
        "get_archive" when a.Length >= 1 => OperatorEngine.GetArchive(a[0]),
        "get_tag" when a.Length >= 2 => OperatorEngine.GetTag(a[0], a[1]),
        "list" => OperatorEngine.List(),
        "sync" when a.Length >= 3 => OperatorEngine.Sync(a[0], a[1], string.Join(' ', a.Skip(2))),
        "new_task" when a.Length >= 2 => OperatorEngine.NewTask(a[0], a[1], a.Length > 2 ? string.Join(' ', a.Skip(2)) : ""),
        "new_project" when a.Length >= 1 => OperatorEngine.NewProject(a[0], a.Length > 1 ? a[1] : null),
        "new_local_project" when a.Length >= 1 => OperatorEngine.NewLocalProject(a[0]),
        "sync_many" when a.Length >= 3 => OperatorEngine.SyncMany(a[0], Pairs(a.Skip(1))),
        "new_subtask" when a.Length >= 2 => OperatorEngine.NewSubtask(a[0], string.Join(' ', a.Skip(1))),
        "subtask_done" when a.Length >= 3 && int.TryParse(a[1], out var sdi) => OperatorEngine.SetSubtaskDone(a[0], sdi, a[2] is "true" or "1"),
        "delete" when a.Length >= 1 => OperatorEngine.Delete(a[0]),
        "archive" when a.Length >= 1 => OperatorEngine.Archive(a[0]),
        "agent_lock" when a.Length >= 2 => OperatorEngine.AgentLock(a[0], a[1]),
        "agent_unlock" when a.Length >= 2 => OperatorEngine.AgentUnlock(a[0], a[1]),
        "move" when a.Length >= 2 && int.TryParse(a[1], out var i) => OperatorEngine.Move(a[0], i),
        "move" => OperatorResult.Fail("move needs: <task_id> <index>"),
        _ => OperatorResult.Fail($"bad or incomplete command. \n{Usage}"),
    };

    private static IEnumerable<KeyValuePair<string, string>> Pairs(IEnumerable<string> items)
    {
        var list = items.ToList();
        for (int i = 0; i + 1 < list.Count; i += 2)
            yield return new KeyValuePair<string, string>(list[i], list[i + 1]);
    }

    private const string Usage = """
        zProject operator

          operator read       <project>
          operator get_archive <project>
          operator get_tag    <task_id> <field>
          operator list
          operator sync       <task_id> <field> <value>
          operator archive    <task_id>
          operator agent_lock <task_id> <key>
          operator agent_unlock <task_id> <key>
          operator new_task   <project> <name> [prompt]
          operator new_project <name> [directory]
          operator new_subtask <task_id> <text...>
          operator subtask_done <task_id> <index> <true|false>
          operator sync_many  <task_id> <field> <value> [<field> <value> ...]
          operator delete     <task_id>
          operator move       <task_id> <index>

        <project> is a project name, id prefix, or directory path.
        <field>   is a tasks.xml element name, e.g. done, inProgress, locked,
                  bug, notes, filesChanged, blockedBy, dueDate.
        """;
}
