using System.Xml.Linq;

namespace PromptQueue.Core.Operator;

/// <summary>
/// One pending change in the operator queue: a command plus its positional
/// string arguments, and (for <c>upsert</c>) an XML payload. Persisted to
/// <c>queue.xml</c> and removed once applied.
/// </summary>
public sealed class OperatorJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string CreatedUtc { get; init; } = DateTime.UtcNow.ToString("o");
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = new();

    /// <summary>Raw XML for the <c>upsert</c> command (a single &lt;task&gt; element).</summary>
    public string Payload { get; set; } = "";

    public OperatorJob() { }

    public OperatorJob(string command, params string[] args)
    {
        Command = command;
        Args = args.ToList();
    }

    /// <summary>The nth argument, or empty string when absent.</summary>
    public string Arg(int index) => index >= 0 && index < Args.Count ? Args[index] : "";

    public XElement ToElement()
    {
        var el = new XElement("job",
            new XAttribute("id", Id),
            new XAttribute("created", CreatedUtc),
            new XAttribute("command", Command));
        foreach (var a in Args)
            el.Add(new XElement("arg", a));
        if (!string.IsNullOrEmpty(Payload))
            el.Add(new XElement("payload", new XCData(Payload)));
        return el;
    }

    public static OperatorJob FromElement(XElement el) => new()
    {
        Id = (string?)el.Attribute("id") ?? Guid.NewGuid().ToString("N")[..8],
        CreatedUtc = (string?)el.Attribute("created") ?? DateTime.UtcNow.ToString("o"),
        Command = (string?)el.Attribute("command") ?? "",
        Args = el.Elements("arg").Select(a => a.Value).ToList(),
        Payload = el.Element("payload")?.Value ?? "",
    };
}
