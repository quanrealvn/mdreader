using System.Globalization;
using System.Text.Json;

namespace MdReader.Core.Json;

/// <summary>
/// What the first pass found: how many children each container has, in the order the containers open, and whether the
/// document can be rendered as a tree at all.
/// </summary>
/// <param name="ChildCounts">One entry per container, indexed by the order the container's start token appears. The
/// second pass walks the same tokens in the same order, so the same index reaches the same container.</param>
/// <param name="NodeCount">Values in the document (scalars and containers alike).</param>
/// <param name="Error">Set when the document is not JSON; the other fields are then meaningless.</param>
/// <param name="Notice">Set when the document is JSON but too big to show as a tree; the prose to show above its text.</param>
internal sealed record JsonScan(List<int> ChildCounts, int NodeCount, JsonParseError? Error, string? Notice);

/// <summary>
/// Pass one: validate the whole document and measure it, before a single character of HTML is written.
/// </summary>
/// <remarks>
/// <para>Two passes rather than one, because a collapsed node has to say how big it is ("{7 keys}") in the summary
/// that is written <em>before</em> its children. The alternative — building a node tree in memory and walking it —
/// costs several times the document in objects and pointers on a machine the plan says has 512 MB, and buys nothing
/// else.</para>
/// <para>Nothing here recurses: <see cref="Utf8JsonReader"/> is a forward token reader over the bytes and the only
/// state is an explicit list, so attacker-controlled nesting costs a list entry rather than a stack frame. The depth,
/// node and (in the second pass) output budgets are all checked as the walk proceeds, so a hostile document is
/// stopped while it is being read rather than after it has been built.</para>
/// </remarks>
internal static class JsonScanner
{
    private const int CancellationCheckInterval = 4096;

    public static JsonScan Scan(ReadOnlySpan<byte> utf8, JsonRenderOptions options, CancellationToken cancellationToken)
    {
        var counts = new List<int>();
        var stack = new List<Frame>();
        var nodes = 0;
        var tokens = 0;
        var anyToken = false;
        var rootComplete = false;
        var lastToken = JsonTokenType.None;

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            // The check below trips first and reports depth as a limit rather than as a syntax error. This is only
            // the floor under a mistake in that check.
            MaxDepth = options.MaxDepth + 4,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });

        try
        {
            while (!rootComplete && reader.Read())
            {
                if (++tokens % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                anyToken = true;
                lastToken = reader.TokenType;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        // A duplicate name is counted like any other: the document says it twice, so the tree does.
                        counts[stack[^1].CountIndex]++;
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        CountValue(counts, stack, ref nodes, options);
                        if (stack.Count >= options.MaxDepth)
                        {
                            throw new TooBigException(string.Format(
                                CultureInfo.InvariantCulture,
                                "This file nests more than {0} levels deep, which is deeper than the tree can show. Here is its text instead.",
                                options.MaxDepth));
                        }

                        counts.Add(0);
                        stack.Add(new Frame(counts.Count - 1, reader.TokenType == JsonTokenType.StartArray, reader.TokenStartIndex));
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.RemoveAt(stack.Count - 1);
                        rootComplete = stack.Count == 0;
                        break;

                    default:
                        CountValue(counts, stack, ref nodes, options);
                        rootComplete = stack.Count == 0;
                        break;
                }
            }

            if (!rootComplete)
            {
                return Failed(JsonErrors.AtEnd(utf8, ContextOf(utf8, stack, lastToken, anyToken)));
            }

            // Everything after the one top-level value. System.Text.Json throws here rather than returning a token,
            // but both are the same finding and both get the same sentence.
            if (reader.Read())
            {
                return Failed(TrailingContent(utf8, (int)reader.TokenStartIndex));
            }
        }
        catch (JsonException exception)
        {
            return Failed(rootComplete
                ? TrailingContent(utf8, JsonErrors.OffsetOf(utf8, exception))
                : JsonErrors.FromException(utf8, exception, ContextOf(utf8, stack, lastToken, anyToken)));
        }
        catch (TooBigException exception)
        {
            return new JsonScan([], 0, null, exception.Notice);
        }

        return new JsonScan(counts, nodes, null, null);
    }

    /// <summary>The notice for a tree that would produce more HTML than its document's size earns it.</summary>
    public static string OutputTooLargeNotice =>
        "The tree for this file would be larger than the page can hold. Here is its text instead.";

    private static JsonScan Failed(JsonParseError error) => new([], 0, error, null);

    private static void CountValue(List<int> counts, List<Frame> stack, ref int nodes, JsonRenderOptions options)
    {
        if (++nodes > options.MaxNodes)
        {
            throw new TooBigException(string.Format(
                CultureInfo.InvariantCulture,
                "This file holds more than {0:N0} values, which is more than the tree can show at once. Here is its text instead.",
                options.MaxNodes));
        }

        if (stack.Count > 0 && stack[^1].IsArray)
        {
            counts[stack[^1].CountIndex]++;
        }
    }

    private static JsonParseError TrailingContent(ReadOnlySpan<byte> utf8, int offset)
    {
        var message = "there is more text after the end of the first JSON value.";
        if (JsonErrors.LooksLikeJsonLines(utf8))
        {
            message += " This file looks like JSON Lines — one complete value per line — rather than a single JSON document.";
        }

        return JsonErrors.At(utf8, offset, message);
    }

    private static JsonErrorContext ContextOf(ReadOnlySpan<byte> utf8, List<Frame> stack, JsonTokenType lastToken, bool anyToken)
    {
        if (stack.Count == 0)
        {
            return new JsonErrorContext(anyToken, false, false, '\0', 0);
        }

        var top = stack[^1];
        var insideObject = !top.IsArray;
        return new JsonErrorContext(
            anyToken,
            insideObject,
            insideObject && lastToken != JsonTokenType.PropertyName,
            insideObject ? '{' : '[',
            LineOf(utf8, top.StartOffset));
    }

    /// <summary>The 1-based line a byte offset sits on.</summary>
    private static int LineOf(ReadOnlySpan<byte> utf8, long offset)
    {
        var line = 1;
        var end = (int)Math.Clamp(offset, 0, utf8.Length);
        for (var i = 0; i < end; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <param name="CountIndex">This container's slot in <see cref="JsonScan.ChildCounts"/>.</param>
    /// <param name="StartOffset">Byte offset of its start token, for "the array opened on line 4 is still open".</param>
    private readonly record struct Frame(int CountIndex, bool IsArray, long StartOffset);

    /// <summary>A document that is valid JSON but past one of the budgets. Never leaves this assembly.</summary>
    internal sealed class TooBigException(string notice) : Exception(notice)
    {
        /// <summary>The prose to show above the document's text.</summary>
        public string Notice { get; } = notice;
    }
}
