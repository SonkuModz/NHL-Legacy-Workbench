namespace NhlWorkbench.Core.Diagnostics;

/// <summary>
/// Raised internally when a format assumption is violated in a way that indicates
/// truly malformed/unrecoverable input. Reader/writer entry points always catch this
/// (and any other exception) at their outermost boundary and convert it to a
/// <see cref="Result{T}"/> failure — it must never escape into caller code.
/// </summary>
public sealed class ParseException : Exception
{
    public ParseException(string message) : base(message)
    {
    }

    public ParseException(string message, Exception inner) : base(message, inner)
    {
    }
}
