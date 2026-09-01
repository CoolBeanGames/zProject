using System.Xml;
using System.Xml.Linq;
using PromptQueue.Core.Models;

namespace PromptQueue.Core.Serialization;

/// <summary>
/// Reads and writes a project's <c>tasks.xml</c>. The on-disk order of
/// &lt;task&gt; elements is authoritative for queue order, which keeps the file
/// diff-friendly and lets a human or an agent reorder tasks by hand.
/// </summary>
public static class TaskXmlSerializer
{
    public const string FileName = "tasks.xml";

    public sealed record Document(string ProjectName, int NextIndex, List<TaskItem> Tasks);

    public static string Serialize(Project project)
    {
        var root = new XElement("tasks",
            new XAttribute("project", project.Name),
            new XAttribute("nextIndex", project.NextIndex));

        foreach (var task in project.Tasks.OrderBy(t => t.Order))
        {
            root.Add(new XElement("task",
                new XAttribute("id", task.Id),
                new XElement("prompt", task.Prompt),
                new XElement("requirements", task.Requirements),
                new XElement("inProgress", task.InProgress),
                new XElement("done", task.Done),
                new XElement("error", task.Error),
                new XElement("errorMessage", task.ErrorMessage),
                new XElement("commit", task.Commit),
                new XElement("build", task.Build),
                new XElement("release", task.Release),
                new XElement("notes", task.Notes),
                new XElement("filesChanged", task.FilesChanged)));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
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
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("tasks.xml has no root element.");

        var projectName = (string?)root.Attribute("project") ?? "";
        var tasks = new List<TaskItem>();
        int order = 0;

        foreach (var el in root.Elements("task"))
        {
            tasks.Add(new TaskItem
            {
                Id = (string?)el.Attribute("id") ?? "",
                Prompt = (string?)el.Element("prompt") ?? "",
                Requirements = (string?)el.Element("requirements") ?? "",
                InProgress = ParseBool(el.Element("inProgress")),
                Done = ParseBool(el.Element("done")),
                Error = ParseBool(el.Element("error")),
                ErrorMessage = (string?)el.Element("errorMessage") ?? "",
                Commit = ParseBool(el.Element("commit")),
                Build = ParseBool(el.Element("build")),
                Release = ParseBool(el.Element("release")),
                Notes = (string?)el.Element("notes") ?? "",
                FilesChanged = (string?)el.Element("filesChanged") ?? "",
                Order = order++,
            });
        }

        var nextIndex = (int?)root.Attribute("nextIndex")
                        ?? InferNextIndex(projectName, tasks);

        return new Document(projectName, nextIndex, tasks);
    }

    private static bool ParseBool(XElement? el)
        => el != null && bool.TryParse(el.Value.Trim(), out var b) && b;

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
