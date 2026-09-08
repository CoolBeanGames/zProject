using System.Text;
using System.Globalization;
using PromptQueue.Core.Models;

namespace PromptQueue.Core.Serialization;

/// <summary>Stable CSV interchange format for project tasks (ZP-118/ZP-119).</summary>
public static class TaskCsvSerializer
{
    public sealed record ParseResult(IReadOnlyList<TaskItem> Tasks, IReadOnlyList<string> Errors);

    public static readonly string[] Headers =
    {
        "task id", "name", "prompt", "status", "build?", "release?", "commit?",
        "due date", "finish data", "agent notes",
    };

    public static string Serialize(IEnumerable<TaskItem> tasks)
    {
        var rows = new List<string> { Join(Headers) };
        rows.AddRange(tasks
            .Where(task => !task.IsNote && !task.StopExecution)
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

    public static ParseResult Parse(string csv)
    {
        var rows = ParseRows(csv);
        if (rows.Count == 0)
            return new ParseResult(Array.Empty<TaskItem>(), new[] { "The CSV file is empty." });

        var header = rows[0].Select(value => value.TrimStart('\uFEFF').Trim()).ToArray();
        if (header.Length != Headers.Length || !header.SequenceEqual(Headers, StringComparer.OrdinalIgnoreCase))
            return new ParseResult(Array.Empty<TaskItem>(), new[]
            {
                $"Expected headers in this exact order: {string.Join(", ", Headers)}",
            });

        var tasks = new List<TaskItem>();
        var errors = new List<string>();
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var line = rowIndex + 1;
            if (row.Count != Headers.Length)
            {
                errors.Add($"Row {line}: expected {Headers.Length} columns but found {row.Count}.");
                continue;
            }

            var id = row[0].Trim();
            if (id.Length == 0)
            {
                errors.Add($"Row {line}: task id is required.");
                continue;
            }
            if (!TryStatus(row[3], out var done, out var inProgress, out var archived))
            {
                errors.Add($"Row {line}: status must be active, in progress, complete, or archived.");
                continue;
            }
            if (!TryBool(row[4], out var build) || !TryBool(row[5], out var release) ||
                !TryBool(row[6], out var commit))
            {
                errors.Add($"Row {line}: build?, release?, and commit? must be true or false.");
                continue;
            }
            if (!TryDate(row[7], out var dueDate))
            {
                errors.Add($"Row {line}: due date must be blank or yyyy-MM-dd HH:mm.");
                continue;
            }
            if (!TryDate(row[8], out var finishDate))
            {
                errors.Add($"Row {line}: finish data must be blank or yyyy-MM-dd HH:mm.");
                continue;
            }
            if ((done || archived) && finishDate == null)
            {
                errors.Add($"Row {line}: complete and archived tasks require finish data.");
                continue;
            }
            if (!done && finishDate != null)
            {
                errors.Add($"Row {line}: active and in-progress tasks cannot have finish data.");
                continue;
            }

            tasks.Add(new TaskItem
            {
                Id = id,
                Name = row[1],
                Prompt = row[2],
                Done = done,
                InProgress = inProgress,
                Archived = archived,
                Build = build,
                Release = release,
                Commit = commit,
                DueDate = dueDate,
                DateFinished = finishDate,
                Notes = row[9],
                Branch = "main",
            });
        }
        return new ParseResult(tasks, errors);
    }

    private static string Status(TaskItem task) =>
        task.Archived ? "archived" :
        task.Done ? "complete" :
        task.InProgress ? "in progress" :
        "active";

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool TryBool(string value, out bool parsed)
    {
        value = value.Trim();
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) { parsed = true; return true; }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Length == 0) { parsed = false; return true; }
        parsed = false;
        return false;
    }

    private static bool TryStatus(string value, out bool done, out bool inProgress, out bool archived)
    {
        done = inProgress = archived = false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "active": return true;
            case "in progress": inProgress = true; return true;
            case "complete": done = true; return true;
            case "archived": done = archived = true; return true;
            default: return false;
        }
    }

    private static bool TryDate(string value, out DateTime? parsed)
    {
        value = value.Trim();
        if (value.Length == 0) { parsed = null; return true; }
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            parsed = date;
            return true;
        }
        parsed = null;
        return false;
    }

    private static string Join(IEnumerable<string> fields) => string.Join(',', fields.Select(Escape));

    private static string Escape(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static List<List<string>> ParseRows(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;

        void FinishField()
        {
            row.Add(field.ToString());
            field.Clear();
            afterQuote = false;
        }

        void FinishRow()
        {
            FinishField();
            rows.Add(row);
            row = new List<string>();
        }

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                }
                else field.Append(ch);
                continue;
            }

            if (afterQuote && ch is not ',' and not '\r' and not '\n')
                throw new FormatException("A quoted CSV field contains characters after its closing quote.");
            if (ch == ',' ) FinishField();
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                FinishRow();
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == '"') throw new FormatException("A CSV field contains an unexpected quote.");
            else field.Append(ch);
        }

        if (quoted) throw new FormatException("A quoted CSV field is not closed.");
        if (field.Length > 0 || row.Count > 0 || afterQuote) FinishRow();
        return rows;
    }
}
