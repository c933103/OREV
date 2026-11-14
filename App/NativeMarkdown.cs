// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.0
// See LICENSE, including Section 10 and Schedule A.

using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
namespace ExportViewer;
// Render Markdown directly into a native RichTextBox, without a browser or active HTML.
internal static class NativeMarkdown
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    public static void Render(RichTextBox output, string source, Font font)
    {
        output.Clear();
        var fonts = new Dictionary<(FontStyle, bool, int), Font>();
        Font GetFont(FontStyle style, bool code = false, int extra = 0)
        {
            var key = (style, code, extra);
            if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font(code ? "Consolas" : font.FontFamily.Name, font.Size + extra, style);
            return f;
        }
        output.Disposed += (_, _) => { foreach (var f in fonts.Values) f.Dispose(); };
        void Append(string text, FontStyle style = FontStyle.Regular, bool code = false, int extra = 0)
        {
            output.SelectionStart = output.TextLength; output.SelectionLength = 0;
            output.SelectionFont = GetFont(style, code, extra); output.SelectionBackColor = code ? Color.FromArgb(240, 242, 247) : Color.White;
            output.AppendText(text);
        }
        void Inline(Inline? inline, FontStyle style = FontStyle.Regular)
        {
            while (inline != null)
            {
                switch (inline)
                {
                    case LiteralInline literal: Append(literal.Content.ToString(), style); break;
                    case CodeInline code: Append(code.Content, style, true); break;
                    case EmphasisInline emphasis: Inline(emphasis.FirstChild, style | (emphasis.DelimiterCount >= 2 ? FontStyle.Bold : FontStyle.Italic)); break;
                    case LineBreakInline: Append("\n", style); break;
                    case LinkInline link:
                        Inline(link.FirstChild, style | FontStyle.Underline);
                        if (!string.IsNullOrEmpty(link.Url)) Append(" (" + link.Url + ")", style); break;
                    case AutolinkInline auto: Append(auto.Url, style | FontStyle.Underline); break;
                    case ContainerInline container: Inline(container.FirstChild, style); break;
                    default: break;
                }
                inline = inline.NextSibling;
            }
        }
        void Block(Block block, int depth = 0)
        {
            if (depth > 32) return;
            switch (block)
            {
                case HeadingBlock heading:
                    if (heading.Inline != null) Inline(heading.Inline.FirstChild, FontStyle.Bold);
                    Append("\n\n"); break;
                case CodeBlock code:
                    Append(code.Lines.ToString(), FontStyle.Regular, true); Append("\n\n"); break;
                case ListBlock list:
                    int number = int.TryParse(list.OrderedStart, out int n) ? n : 1;
                    foreach (var child in list)
                    {
                        Append(new string(' ', depth * 2) + (list.IsOrdered ? (number++) + ". " : "• "));
                        Block(child, depth + 1);
                    }
                    break;
                case QuoteBlock quote:
                    Append("│ ", FontStyle.Italic); foreach (var child in quote) Block(child, depth + 1); break;
                case ThematicBreakBlock: Append("────────────────\n\n"); break;
                case Table table:
                    foreach (var row in table.OfType<TableRow>())
                    {
                        foreach (var cell in row.OfType<TableCell>()) { foreach (var child in cell) Block(child, depth + 1); Append("\t"); }
                        Append("\n");
                    }
                    break;
                case LeafBlock leaf:
                    if (leaf.Inline != null) Inline(leaf.Inline.FirstChild); else Append(leaf.Lines.ToString());
                    Append("\n\n"); break;
                case ContainerBlock container: foreach (var child in container) Block(child, depth + 1); break;
            }
        }
        try { foreach (var block in Markdown.Parse(source, Pipeline)) Block(block); }
        catch { output.Text = source; }
        if (string.IsNullOrWhiteSpace(output.Text) && !string.IsNullOrWhiteSpace(source)) output.Text = source;
        output.Select(0, 0);
    }
}
