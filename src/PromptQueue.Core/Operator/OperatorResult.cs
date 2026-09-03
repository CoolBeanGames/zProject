namespace PromptQueue.Core.Operator;

/// <summary>Outcome of an <see cref="OperatorEngine"/> call.</summary>
public sealed record OperatorResult(bool Ok, string Message, string? Output = null)
{
    public static OperatorResult Pass(string message, string? output = null) => new(true, message, output);

    public static OperatorResult Fail(string message) => new(false, message);
}
