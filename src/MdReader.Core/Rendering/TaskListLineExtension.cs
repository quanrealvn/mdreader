using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MdReader.Core.Rendering;

/// <summary>
/// Replaces Markdig's task-list renderer so a checkbox the pipeline produced can carry the source line of its list item
/// (ARCHITECTURE §4.1). The value is written as <c>&lt;per-render token&gt;:&lt;1-based line&gt;</c>; the sanitizer keeps
/// the attribute only when the token matches, and emits the bare line number. Raw HTML from the document can't guess the
/// token, so an author-written <c>&lt;input data-line="3"&gt;</c> stays a plain disabled checkbox.
/// </summary>
internal sealed class TaskListLineExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
        {
            return;
        }

        // Replace in place (rather than append) so the task-list renderer still wins over the generic inline renderers.
        for (var i = 0; i < html.ObjectRenderers.Count; i++)
        {
            if (html.ObjectRenderers[i] is HtmlTaskListRenderer)
            {
                html.ObjectRenderers[i] = new TaskListLineRenderer();
                return;
            }
        }

        html.ObjectRenderers.Add(new TaskListLineRenderer());
    }
}

/// <summary>Markdig's own task-list output plus the <c>data-line</c> property the AST pass attached (if any).</summary>
internal sealed class TaskListLineRenderer : HtmlObjectRenderer<TaskList>
{
    /// <summary>The attribute the AST pass sets on a <see cref="TaskList"/> and the writer validates.</summary>
    internal const string LineAttribute = "data-line";

    protected override void Write(HtmlRenderer renderer, TaskList obj)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(obj);

        if (!renderer.EnableHtmlForInline)
        {
            renderer.Write(obj.Checked ? "[x]" : "[ ]");
            return;
        }

        renderer.Write("<input disabled=\"disabled\" type=\"checkbox\"");
        if (obj.Checked)
        {
            renderer.Write(" checked=\"checked\"");
        }

        if (obj.TryGetAttributes() is { Properties: { } properties })
        {
            foreach (var property in properties)
            {
                if (string.Equals(property.Key, LineAttribute, StringComparison.Ordinal) && property.Value is { } value)
                {
                    renderer.Write(" ").Write(LineAttribute).Write("=\"").Write(value).Write('"');
                }
            }
        }

        renderer.Write(" />");
    }
}
