using System.Text;
using PromptQueue.Core.Models;

namespace PromptQueue.Core.Serialization;

/// <summary>Stable CSV interchange format for project tasks (ZP-118/ZP-119).</summary>
public static class TaskCsvSerializer
{
    public static readonly string[] Headers =
    {
        "task id", "name", "prompt", "status", "build?", "release?", "commit?",
        "due date", "finish data", "agent notes",
    };

    public static string Serialize(IEnumerable<TaskItem> tasks)
    {
        var rows = new List<string> { Join(Headers) };
        rows.AddRange(tasks
            .Where(task => !task.IsNote)
            .Select(task => Join(new[]
            {
                task.Id,
                task.Name,
                task.Prompt,
                Status(task),
                Bool(task.Build),
                Bool(task.Release),
                Bool(task.Commit),
                task.DueDateText,
                task.DateFinishedText,
                task.Notes,
            })));
        return string.Join("\r\n", rows) + "\r\n";
    }

    public static byte[] SerializeUtf8(IEnumerable<TaskItem> tasks)
    {
        var body = Encoding.UTF8.GetBytes(Serialize(tasks));
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static string Status(TaskItem task) =>
        task.Archived ? "archived" :
        task.Done ? "complete" :
        task.InProgress ? "in progress" :
        "active";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Join(IEnumerable<string> fields) => string.Join(',', fields.Select(Escape));

    private static string Escape(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
