using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DevOpsAgent.Web;

/// <summary>
/// Assistant messages as Markdown (T8.5, noted in T7.R10): code blocks, lists, tables, emphasis and links. A model's text is
/// untrusted, so raw HTML is not rendered (Markdig escapes it), only http(s) and mailto links survive, and images are shown
/// as links instead of being loaded.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    public static string ToSafeHtml(string? markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "#";
            link.IsImage = false; // no remote image is fetched from model output
        }

        foreach (var link in document.Descendants<AutolinkInline>())
            if (!IsSafeUrl(link.Url)) link.Url = "#";

        using var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith('#')) return true;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
    }
}
