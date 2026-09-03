using System.Globalization;
using PromptQueue.Core.Models;

namespace PromptQueue.Core.Operator;

/// <summary>
/// Maps a field name (as used by <c>operator sync &lt;id&gt; &lt;field&gt; &lt;value&gt;</c>)
/// onto the matching <see cref="TaskItem"/> property. Names match the tasks.xml
/// element names, case-insensitively, with a few friendly aliases.
/// </summary>
public static class TaskFields
{
    private static readonly Dictionary<string, Action<TaskItem, string>> Setters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = (t, v) => t.Name = v,
            ["prompt"] = (t, v) => t.Prompt = v,
            ["requirements"] = (t, v) => t.Requirements = v,
            ["inprogress"] = (t, v) => t.InProgress = Bool(v),
            ["done"] = (t, v) => t.Done = Bool(v),
            ["bug"] = (t, v) => t.Bug = Bool(v),
            ["error"] = (t, v) => t.Error = Bool(v),
            ["errormessage"] = (t, v) => t.ErrorMessage = v,
            ["lockkey"] = (t, v) => t.LockKey = v.Trim(),
            ["locked"] = (t, v) => t.Locked = Bool(v),
            ["archived"] = (t, v) => t.Archived = Bool(v),
            ["blockedby"] = (t, v) => t.BlockedBy = v.Trim(),
            ["datestarted"] = (t, v) => t.DateStarted = Date(v),
            ["duedate"] = (t, v) => t.DueDate = Date(v),
            ["commit"] = (t, v) => t.Commit = Bool(v),
            ["build"] = (t, v) => t.Build = Bool(v),
            ["release"] = (t, v) => t.Release = Bool(v),
            ["tags"] = (t, v) => t.TagText = v,
            ["notes"] = (t, v) => t.Notes = v,
            ["fileschanged"] = (t, v) => t.FilesChanged = v,
            ["image"] = (t, v) => t.Image = v.Trim(),
        };

    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["in_progress"] = "inprogress",
            ["error_message"] = "errormessage",
            ["lock_key"] = "lockkey",
            ["blocked_by"] = "blockedby",
            ["date_started"] = "datestarted",
            ["due_date"] = "duedate",
            ["due"] = "duedate",
            ["files_changed"] = "fileschanged",
            ["files"] = "fileschanged",
            ["tag"] = "tags",
            ["note"] = "notes",
        };

    /// <summary>The set of recognised field names (canonical spelling).</summary>
    public static IEnumerable<string> Names => Setters.Keys;

    private static readonly Dictionary<string, Func<TaskItem, string>> Getters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = t => t.Name,
            ["prompt"] = t => t.Prompt,
            ["requirements"] = t => t.Requirements,
            ["inprogress"] = t => t.InProgress ? "true" : "false",
            ["done"] = t => t.Done ? "true" : "false",
            ["bug"] = t => t.Bug ? "true" : "false",
            ["error"] = t => t.Error ? "true" : "false",
            ["errormessage"] = t => t.ErrorMessage,
            ["lockkey"] = t => t.LockKey,
            ["locked"] = t => t.Locked ? "true" : "false",
            ["archived"] = t => t.Archived ? "true" : "false",
            ["blockedby"] = t => t.BlockedBy,
            ["datestarted"] = t => t.DateStartedText,
            ["duedate"] = t => t.DueDateText,
            ["commit"] = t => t.Commit ? "true" : "false",
            ["build"] = t => t.Build ? "true" : "false",
            ["release"] = t => t.Release ? "true" : "false",
            ["tags"] = t => t.TagText,
            ["notes"] = t => t.Notes,
            ["fileschanged"] = t => t.FilesChanged,
            ["image"] = t => t.Image,
        };

    /// <summary>Reads one field's value, or null if the field name is unknown.</summary>
    public static string? Read(TaskItem task, string field)
    {
        field = field.Trim();
        if (Aliases.TryGetValue(field, out var canonical))
            field = canonical;
        return Getters.TryGetValue(field, out var getter) ? getter(task) : null;
    }

    /// <summary>Applies <paramref name="value"/> to <paramref name="field"/>; false if the field is unknown.</summary>
    public static bool Apply(TaskItem task, string field, string value)
    {
        field = field.Trim();
        if (Aliases.TryGetValue(field, out var canonical))
            field = canonical;
        if (!Setters.TryGetValue(field, out var setter))
            return false;
        setter(task, value);
        return true;
    }

    private static bool Bool(string v) =>
        v.Trim() is "1" or "yes" or "y" or "on"
        || (bool.TryParse(v.Trim(), out var b) && b);

    private static DateTime? Date(string v)
    {
        v = v.Trim();
        if (v.Length == 0)
            return null;
        return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}
