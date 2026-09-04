using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PromptQueue.Core.Models;

namespace PromptQueue.Core.Serialization;

/// <summary>
/// Thrown when a <c>tasks.xml</c> cannot be parsed even after the lenient
/// repair pass. Carries the underlying parser message for display.
/// </summary>
public sealed class TaskXmlFormatException : Exception
{
    public TaskXmlFormatException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Reads and writes a project's <c>tasks.xml</c>. The on-disk order of
/// &lt;task&gt; elements is authoritative for queue order, which keeps the file
/// diff-friendly and lets a human or an agent reorder tasks by hand.
/// </summary>
public static class TaskXmlSerializer
{
    public const string FileName = "tasks.xml";

    /// <summary>Sibling of <see cref="FileName"/> that holds archived tasks only (ZP-71).</summary>
    public const string ArchiveFileName = "archive.xml";

    public sealed record Document(string ProjectName, int NextIndex, List<TaskItem> Tasks);

    /// <summary>Builds the <c>&lt;tasks&gt;</c> element for a project (also reused inside data.cfg).</summary>
    public static XElement ToElement(Project project)
        => ToElement(project, project.Tasks);

    /// <summary>Builds the <c>&lt;tasks&gt;</c> element for an explicit task list (ZP-71: split active / archive).</summary>
    public static XElement ToElement(Project project, IEnumerable<TaskItem> tasks)
    {
        var root = new XElement("tasks",
            new XAttribute("project", project.Name),
            new XAttribute("nextIndex", project.NextIndex));

        // Bugs are written first regardless of their position in the list (ZP-23),
        // otherwise on-disk order follows the queue order.
        foreach (var task in tasks
                     .OrderByDescending(t => t.Bug)
                     .ThenBy(t => t.Order))
        {
            root.Add(TaskToElement(task));
        }

        return root;
    }

    private static XElement TaskToElement(TaskItem task)
    {
        var el = new XElement("task",
            new XAttribute("id", task.Id),
            new XElement("name", task.Name),
            new XElement("prompt", task.Prompt),
            new XElement("requirements", task.Requirements),
            new XElement("inProgress", task.InProgress),
            new XElement("done", task.Done),
            new XElement("bug", task.Bug),
            new XElement("error", task.Error),
            new XElement("errorMessage", task.ErrorMessage),
            new XElement("lockKey", task.LockKey),
            new XElement("locked", task.Locked),
            new XElement("archived", task.Archived),
            new XElement("blockedBy", task.BlockedBy),
            new XElement("dateStarted", FormatDate(task.DateStarted)),
            new XElement("dueDate", FormatDate(task.DueDate)),
            new XElement("commit", task.Commit),
            new XElement("build", task.Build),
            new XElement("release", task.Release),
            new XElement("merge", task.Merge),
            new XElement("branch", task.Branch),
            new XElement("tags", task.TagText),
            new XElement("notes", task.Notes),
            new XElement("filesChanged", task.FilesChanged),
            new XElement("image", task.Image));

        if (task.Subtasks.Count > 0)
        {
            var subs = new XElement("subtasks");
            foreach (var s in task.Subtasks)
                subs.Add(new XElement("subtask", new XAttribute("done", s.Done), s.Text));
            el.Add(subs);
        }

        return el;
    }

    public static string Serialize(Project project)
        => Serialize(project, project.Tasks);

    public static string Serialize(Project project, IEnumerable<TaskItem> tasks)
    {
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), ToElement(project, tasks));
        using var sw = new Utf8StringWriter();
        using (var xw = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = System.Text.Encoding.UTF8,
        }))
        {
            doc.Save(xw);
        }
        return sw.ToString();
    }

    public static Document Deserialize(string xml)
    {
        var doc = ParseTolerant(xml);
        var root = doc.Root ?? throw new TaskXmlFormatException("tasks.xml has no root element.");

        var projectName = (string?)root.Attribute("project") ?? "";
        var tasks = new List<TaskItem>();
        int order = 0;

        foreach (var el in root.Elements("task"))
        {
            var task = new TaskItem
            {
                Id = (string?)el.Attribute("id") ?? "",
                Name = (string?)el.Element("name") ?? "",
                Prompt = (string?)el.Element("prompt") ?? "",
                Requirements = (string?)el.Element("requirements") ?? "",
                InProgress = ParseBool(el.Element("inProgress")),
                Done = ParseBool(el.Element("done")),
                Bug = ParseBool(el.Element("bug")),
                Error = ParseBool(el.Element("error")),
                ErrorMessage = (string?)el.Element("errorMessage") ?? "",
                LockKey = ((string?)el.Element("lockKey") ?? "").Trim(),
                Locked = ParseBool(el.Element("locked")),
                Archived = ParseBool(el.Element("archived")),
                BlockedBy = ((string?)el.Element("blockedBy") ?? "").Trim(),
                DateStarted = ParseDate(el.Element("dateStarted")),
                DueDate = ParseDate(el.Element("dueDate")),
                Commit = ParseBool(el.Element("commit")),
                Build = ParseBool(el.Element("build")),
                Release = ParseBool(el.Element("release")),
                Merge = ParseBool(el.Element("merge")),
                Branch = ((string?)el.Element("branch") ?? "").Trim(),
                TagText = (string?)el.Element("tags") ?? "",
                Notes = (string?)el.Element("notes") ?? "",
                FilesChanged = (string?)el.Element("filesChanged") ?? "",
                Image = ((string?)el.Element("image") ?? "").Trim(),
                Order = order++,
            };

            foreach (var s in el.Element("subtasks")?.Elements("subtask") ?? Enumerable.Empty<XElement>())
            {
                task.Subtasks.Add(new Subtask
                {
                    Text = s.Value,
                    Done = bool.TryParse(((string?)s.Attribute("done"))?.Trim(), out var d) && d,
                });
            }

            tasks.Add(task);
        }

        var nextIndex = (int?)root.Attribute("nextIndex")
                        ?? InferNextIndex(projectName, tasks);

        return new Document(projectName, nextIndex, tasks);
    }

    /// <summary>
    /// Parses <c>tasks.xml</c>, and if that fails, tries once more after a
    /// lenient repair pass that re-escapes stray <c>&amp;</c> / <c>&lt;</c> /
    /// <c>&gt;</c> inside the known free-text elements. Agents hand-edit this file
    /// and routinely paste command lines, generics and code fragments into
    /// &lt;notes&gt; without escaping them (see ZP-52); one such slip would
    /// otherwise take the whole app down on startup.
    /// </summary>
    private static XDocument ParseTolerant(string xml)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (XmlException first)
        {
            try
            {
                return XDocument.Parse(RepairMarkup(xml));
            }
            catch (XmlException)
            {
                throw new TaskXmlFormatException(first.Message, first);
            }
        }
    }

    /// <summary>Elements whose content is free text an agent might not escape.</summary>
    private static readonly string[] TextElements =
    {
        "name", "prompt", "requirements", "errorMessage",
        "blockedBy", "tags", "notes", "filesChanged", "lockKey", "image",
    };

    private static string RepairMarkup(string xml)
    {
        // 1. Bare ampersands that are not part of an entity reference.
        xml = Regex.Replace(
            xml, @"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", "&amp;");

        // 2. Stray < / > inside the known leaf elements. The replacement is
        //    idempotent, so content that was already well-formed is untouched.
        foreach (var tag in TextElements)
        {
            xml = Regex.Replace(
                xml,
                $"(<{tag}>)(.*?)(</{tag}>)",
                m => m.Groups[1].Value
                     + m.Groups[2].Value.Replace("<", "&lt;").Replace(">", "&gt;")
                     + m.Groups[3].Value,
                RegexOptions.Singleline);
        }

        // 3. Stray < / > inside <subtask ...>text</subtask> bodies.
        xml = Regex.Replace(
            xml,
            @"(<subtask\b[^>]*>)(.*?)(</subtask>)",
            m => m.Groups[1].Value
                 + m.Groups[2].Value.Replace("<", "&lt;").Replace(">", "&gt;")
                 + m.Groups[3].Value,
            RegexOptions.Singleline);

        return xml;
    }

    private static bool ParseBool(XElement? el)
        => el != null && bool.TryParse(el.Value.Trim(), out var b) && b;

    private const string DateFmt = "yyyy-MM-dd HH:mm";

    private static string FormatDate(DateTime? d)
        => d?.ToString(DateFmt, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static DateTime? ParseDate(XElement? el)
    {
        var s = el?.Value.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    /// <summary>
    /// Falls back to the highest numeric suffix seen among task ids + 1 when the
    /// file predates the nextIndex attribute.
    /// </summary>
    private static int InferNextIndex(string projectName, List<TaskItem> tasks)
    {
        var prefix = IdGenerator.PrefixFor(projectName) + "-";
        int max = 0;
        foreach (var t in tasks)
        {
            if (t.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(t.Id[prefix.Length..], out var n))
            {
                max = Math.Max(max, n);
            }
        }
        return max + 1;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
}
