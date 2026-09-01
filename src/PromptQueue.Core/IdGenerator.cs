using System.Text;

namespace PromptQueue.Core;

/// <summary>
/// Derives a task-id prefix from a project name: the first letter of every
/// word, upper-cased. Word boundaries are whitespace, punctuation and
/// camelCase / PascalCase humps, so "My Cool App", "my-cool-app" and
/// "MyCoolApp" all yield "MCA".
/// </summary>
public static class IdGenerator
{
    public static string PrefixFor(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "P";

        var sb = new StringBuilder();
        bool atWordStart = true;
        char prev = '\0';

        foreach (var c in projectName)
        {
            if (char.IsLetterOrDigit(c))
            {
                bool camelHump = char.IsUpper(c) && char.IsLower(prev);
                bool digitBoundary = char.IsDigit(c) && char.IsLetter(prev);
                if (atWordStart || camelHump || digitBoundary)
                    sb.Append(char.ToUpperInvariant(c));
                atWordStart = false;
            }
            else
            {
                atWordStart = true;
            }
            prev = c;
        }

        return sb.Length > 0 ? sb.ToString() : "P";
    }
}
