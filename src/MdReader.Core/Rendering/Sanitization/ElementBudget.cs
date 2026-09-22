using AngleSharp;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// Caps the number of HTML elements AngleSharp creates while parsing one document (<see cref="RenderLimits.ElementBudget"/>).
/// </summary>
/// <remarks>
/// <para><c>HtmlParserOptions.OnCreated</c> is not enough: it fires only for elements written in the source, not for the
/// clones made by "reconstruct the active formatting elements" or the adoption agency (verified: 1,204 callbacks for a
/// 201,204-element DOM), and those clones are exactly the amplification. Every HTML element, clone or not, is created by
/// the browsing context's <see cref="IElementFactory{TDocument, TElement}"/>, so the parser used for sanitizing runs in a
/// context whose element factory charges the budget of the current thread (parsing is synchronous on the calling
/// thread). Throwing <see cref="RenderLimitExceededException"/> from the factory aborts <c>SanitizeDom</c>.</para>
/// <para>The context is otherwise HtmlSanitizer's default (<c>Configuration.Default.WithCss</c> with HtmlSanitizer's CSS
/// parser options) and the parser uses HtmlSanitizer's default parser options (scripting enabled).</para>
/// </remarks>
internal sealed class ElementBudget : IDisposable
{
    [ThreadStatic]
    private static ElementBudget? t_current;

    private static readonly HtmlParserOptions ParserOptions = HtmlSanitizer.DefaultHtmlParserFactory().Options;

    private static readonly IBrowsingContext ParserContext = CreateParserContext();

    private readonly ElementBudget? _previous;
    private long _remaining;

    private ElementBudget(long limit, ElementBudget? previous)
    {
        _remaining = limit;
        _previous = previous;
    }

    /// <summary>
    /// A parser for HtmlSanitizer (<c>HtmlSanitizer.HtmlParserFactory</c>) whose elements are charged to the budget that is
    /// current on the parsing thread (unlimited if none).
    /// </summary>
    public static HtmlParser CreateParser() => new(ParserOptions, ParserContext);

    /// <summary>Makes a budget of <paramref name="limit"/> elements current on this thread until disposed.</summary>
    public static ElementBudget Enter(long limit)
    {
        var budget = new ElementBudget(limit, t_current);
        t_current = budget;
        return budget;
    }

    public void Dispose()
    {
        if (ReferenceEquals(t_current, this))
        {
            t_current = _previous;
        }
    }

    private static IBrowsingContext CreateParserContext()
    {
        // HtmlSanitizer's default configuration: Configuration.Default + CSS with these parser options.
        var configuration = Configuration.Default.WithCss(new CssParserOptions
        {
            IsIncludingUnknownDeclarations = true,
            IsIncludingUnknownRules = true,
            IsToleratingInvalidSelectors = true,
        });

        // Wrap the stock HTML element factory (the type itself is internal to AngleSharp).
        var inner = configuration.Services.OfType<IElementFactory<Document, HtmlElement>>().Single();
        return BrowsingContext.New(configuration.WithOnly<IElementFactory<Document, HtmlElement>>(new BudgetedElementFactory(inner)));
    }

    private static void Charge()
    {
        if (t_current is { } budget && --budget._remaining < 0)
        {
            throw new RenderLimitExceededException("The HTML creates too many elements (formatting-element amplification).");
        }
    }

    private sealed class BudgetedElementFactory(IElementFactory<Document, HtmlElement> inner) : IElementFactory<Document, HtmlElement>
    {
        public HtmlElement Create(Document document, string localName, string? prefix = null, NodeFlags flags = NodeFlags.None)
        {
            Charge();
            return inner.Create(document, localName, prefix, flags);
        }
    }
}
