namespace ShortForge.Core;

public sealed class UserError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
