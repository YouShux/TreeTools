using Markdig;
using Markdig.Syntax.Inlines;
using OmenTools.ImGuiOm.Markdown;
using OmenTools.ImGuiOm.Markdown.Layout;

namespace TreeTools.Text;

public sealed class MarkdownHtmlEntityExtension : IImGuiMarkdownExtension
{
    public void ConfigurePipeline(MarkdownPipelineBuilder builder) { }

    public bool TryBuildInline(Inline inline, InlineRun style, List<InlineRun> runs)
    {
        if (inline is not HtmlEntityInline entity)
            return false;

        runs.Add(style with { Text = entity.Transcoded.ToString() });
        return true;
    }
}
