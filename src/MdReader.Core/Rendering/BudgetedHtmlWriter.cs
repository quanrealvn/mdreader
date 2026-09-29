using System.Text;

namespace MdReader.Core.Rendering;

/// <summary>
/// The <see cref="TextWriter"/> Markdig renders into: a string builder that throws
/// <see cref="RenderLimitExceededException"/> as soon as the HTML grows past its budget
/// (<see cref="RenderLimits.MarkdownHtmlBudget"/>), so Markdig's own amplification (e.g. one long reference-link
/// destination repeated by thousands of <c>[a]</c> references) is stopped while it happens, not after the memory is gone.
/// Every write goes through <see cref="Write(char)"/>, <see cref="Write(char[], int, int)"/>,
/// <see cref="Write(ReadOnlySpan{char})"/> or <see cref="Write(string)"/>; the base class routes all other overloads
/// (WriteLine, formatted writes) to those.
/// <para>The same check observes the caller's cancellation token. Markdig writes the whole document in one
/// uncancellable call, so this is the only place a deadline can stop it.</para>
/// </summary>
internal sealed class BudgetedHtmlWriter : TextWriter
{
    /// <summary>Writes between two cancellation checks (a write is a tag, an attribute or a run of text).</summary>
    private const int CancellationCheckInterval = 4096;

    private readonly StringBuilder _builder;
    private readonly long _budget;
    private readonly CancellationToken _cancellationToken;
    private int _untilCheck = CancellationCheckInterval;

    public BudgetedHtmlWriter(long budget, int initialCapacity, CancellationToken cancellationToken = default)
    {
        _budget = budget;
        _cancellationToken = cancellationToken;
        _builder = new StringBuilder(Math.Max(16, initialCapacity));
    }

    public override Encoding Encoding => Encoding.Unicode;

    /// <summary>Characters written so far.</summary>
    public int Length => _builder.Length;

    public override void Write(char value)
    {
        _builder.Append(value);
        Check();
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _builder.Append(buffer, index, count);
        Check();
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        _builder.Append(buffer);
        Check();
    }

    public override void Write(string? value)
    {
        _builder.Append(value);
        Check();
    }

    public override string ToString() => _builder.ToString();

    private void Check()
    {
        if (_builder.Length > _budget)
        {
            throw new RenderLimitExceededException("Markdig's HTML would be too large for the document (reference or footnote amplification).");
        }

        if (--_untilCheck <= 0)
        {
            _untilCheck = CancellationCheckInterval;
            _cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
