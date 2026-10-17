using Agentivity.Domain.Workflow;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.LayoutAdapters;

/// <summary>
/// Converts DSL PageNode to email-compatible HTML.
/// Uses table-based layout with inline styles for maximum email client compatibility (including Outlook).
/// </summary>
public sealed class DslToEmailHtmlNode : DslAdapterNode
{
    public DslToEmailHtmlNode()
        : base("content.layoutadapters.dsl_to_email_html")
    {
        DefineMetadata("DSL to Email HTML", "Converts DSL content to email-compatible HTML with table layout and inline styles", "content.layoutadapters", whenToUse: "To turn a page layout in DSL format into the HTML of an e-mail, such as a newsletter, that displays well in mail apps, in your brand colors. For a web page, use DSL to HTML instead.");
        DefineEffects(NodeEffects.None);

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Input
        DefineInput(
            "dsl_content",
            ArgumentType.Text,
            "Page layout in DSL format (JSON), as Content Generator gives it",
            ui: new ArgumentUiMetadata
            {
                Tab = "Content",
                Group = "Source",
                Order = 10,
                Multiline = true,
                Rows = 8,
                Format = "json",
            },
            required: true
        );

        DefineInput(
            "email_width",
            ArgumentType.Number,
            "Email width in pixels (default: 600)",
            600,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Layout",
                Order = 20,
            },
            required: false
        );

        DefineInput(
            "brand_color",
            ArgumentType.Text,
            "Primary brand color in hex (default: #2563EB)",
            "#2563EB",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Styling",
                Order = 30,
            },
            required: false
        );

        DefineInput(
            "background_color",
            ArgumentType.Text,
            "Background color in hex (default: #F3F4F6)",
            "#F3F4F6",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Styling",
                Order = 40,
            },
            required: false
        );

        DefineInput(
            "text_color",
            ArgumentType.Text,
            "Text color in hex (default: #1F2937)",
            "#1F2937",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Styling",
                Order = 50,
            },
            required: false
        );

        DefineInput(
            "font_family",
            ArgumentType.Text,
            "Font family (default: Arial, Helvetica, sans-serif)",
            "Arial, Helvetica, sans-serif",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Styling",
                Order = 60,
            },
            required: false
        );

        DefineInput(
            "include_responsive",
            ArgumentType.Boolean,
            "Include media queries for mobile responsive (default: true)",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Output",
                Order = 70,
            },
            required: false
        );

        // Output
        DefineOutput("email_html", ArgumentType.Text, "Generated email-compatible HTML");

        DefineOutput("preview_text", ArgumentType.Text, "Preview text (first 100 characters of content)");

        DefineOutput("metadata", ArgumentType.Text, "Content metadata");

        DefineIcon(NodeIcon.Embedded("email"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Parsing DSL content...");
        ArgumentInputValues args = GetInputs(config);

        int emailWidth = args.GetInt("email_width") ?? 0;
        if (emailWidth <= 0)
            emailWidth = 600;

        string brandColor = args.GetText("brand_color") ?? "#2563EB";
        string backgroundColor = args.GetText("background_color") ?? "#F3F4F6";
        string textColor = args.GetText("text_color") ?? "#1F2937";
        string fontFamily = args.GetText("font_family") ?? "Arial, Helvetica, sans-serif";
        bool includeResponsive = args.GetBoolean("include_responsive") ?? true;

        DslNode dslContent = GetDslContent(args);

        services.PublishStatus("Converting DSL to email HTML...");
        var converter = new EmailHtmlConverter(emailWidth, brandColor, backgroundColor, textColor, fontFamily, includeResponsive);

        string emailHtml = converter.ConvertToEmailHtml(dslContent);
        string previewText = converter.GetPreviewText();

        services.PublishStatus("Extracting metadata...");
        EmailMetadata metadata = ExtractMetadata(dslContent);

        services.PublishStatus($"Email HTML conversion completed ({metadata.WordCount} words).");

        string metadataJson = JsonSerializer.Serialize(metadata, SerializerOptions);

        return ValueTask.FromResult(NodeResult.Create().AddText("email_html", emailHtml).AddText("preview_text", previewText).AddText("metadata", metadataJson));
    }

    private static EmailMetadata ExtractMetadata(DslNode dslNode)
    {
        string textContent = ExtractTextContent(dslNode);
        int wordCount = CountWords(textContent);
        List<(int Level, string Text, string? Id)> headings = CollectHeadings(dslNode);

        return new EmailMetadata(HeadingCount: headings.Count, WordCount: wordCount, EstimatedReadingMinutes: Math.Max(1, wordCount / 200));
    }

    private sealed record EmailMetadata(int HeadingCount, int WordCount, int EstimatedReadingMinutes);

    private sealed class EmailHtmlConverter
    {
        private readonly int _emailWidth;
        private readonly string _brandColor;
        private readonly string _backgroundColor;
        private readonly string _textColor;
        private readonly string _fontFamily;
        private readonly bool _includeResponsive;
        private readonly StringBuilder _previewTextBuilder = new();
        private int _previewTextLength = 0;

        public EmailHtmlConverter(int emailWidth, string brandColor, string backgroundColor, string textColor, string fontFamily, bool includeResponsive)
        {
            _emailWidth = emailWidth;
            _brandColor = brandColor;
            _backgroundColor = backgroundColor;
            _textColor = textColor;
            _fontFamily = fontFamily;
            _includeResponsive = includeResponsive;
        }

        public string ConvertToEmailHtml(DslNode dslNode)
        {
            var sb = new StringBuilder();

            // DOCTYPE and HTML opening
            _ = sb.AppendLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
            _ = sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            _ = sb.AppendLine("<head>");
            _ = sb.AppendLine("  <meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\" />");
            _ = sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
            _ = sb.AppendLine("  <title>Email</title>");

            // Responsive CSS (if enabled)
            if (_includeResponsive)
            {
                _ = sb.AppendLine("  <style type=\"text/css\">");
                _ = sb.AppendLine("    @media only screen and (max-width: 600px) {");
                _ = sb.AppendLine("      .email-container { width: 100% !important; }");
                _ = sb.AppendLine("      .mobile-padding { padding: 10px !important; }");
                _ = sb.AppendLine("      .mobile-hide { display: none !important; }");
                _ = sb.AppendLine("      h1 { font-size: 24px !important; }");
                _ = sb.AppendLine("      h2 { font-size: 20px !important; }");
                _ = sb.AppendLine("      h3 { font-size: 18px !important; }");
                _ = sb.AppendLine("    }");
                _ = sb.AppendLine("  </style>");
            }

            _ = sb.AppendLine("</head>");
            _ = sb.AppendLine("<body style=\"margin: 0; padding: 0; background-color: " + _backgroundColor + ";\">");

            // Outer wrapper table
            _ = sb.AppendLine("  <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color: " + _backgroundColor + ";\">");
            _ = sb.AppendLine("    <tr>");
            _ = sb.AppendLine("      <td align=\"center\" style=\"padding: 20px 0;\">");

            // Main email container table
            _ = sb.AppendLine("        <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"" + _emailWidth + "\" class=\"email-container\" style=\"background-color: #FFFFFF; border-radius: 8px; overflow: hidden;\">");
            _ = sb.AppendLine("          <tr>");
            _ = sb.AppendLine("            <td style=\"padding: 40px 30px;\" class=\"mobile-padding\">");

            // Content
            ConvertNode(dslNode, sb, 0);

            _ = sb.AppendLine("            </td>");
            _ = sb.AppendLine("          </tr>");
            _ = sb.AppendLine("        </table>");

            _ = sb.AppendLine("      </td>");
            _ = sb.AppendLine("    </tr>");
            _ = sb.AppendLine("  </table>");

            _ = sb.AppendLine("</body>");
            _ = sb.AppendLine("</html>");

            return sb.ToString();
        }

        public string GetPreviewText()
        {
            string preview = _previewTextBuilder.ToString().Trim();
            if (preview.Length > 100)
            {
                preview = preview.Substring(0, 100) + "...";
            }
            return preview;
        }

        private void ConvertNode(DslNode node, StringBuilder sb, int depth)
        {
            switch (node)
            {
                case HeadingNode heading:
                    ConvertHeading(heading, sb);
                    break;

                case TextNode text:
                    ConvertText(text, sb);
                    break;

                case SectionNode section:
                    ConvertSection(section, sb, depth);
                    break;

                case ContainerNode container:
                    ConvertContainer(container, sb, depth);
                    break;

                case BlockNode block:
                    ConvertBlock(block, sb, depth);
                    break;

                case ListNode list:
                    ConvertList(list, sb);
                    break;

                case ImageNode image:
                    ConvertImage(image, sb);
                    break;

                case ButtonNode button:
                    ConvertButton(button, sb);
                    break;

                case TocNode:
                    // TOC not typically useful in emails, skip
                    break;

                case SpacerNode spacer:
                    ConvertSpacer(spacer, sb);
                    break;

                case DividerNode divider:
                    ConvertDivider(divider, sb);
                    break;
            }
        }

        private void ConvertHeading(HeadingNode heading, StringBuilder sb)
        {
            int level = Math.Clamp(heading.Level, 1, 6);
            string fontSize = level switch
            {
                1 => "28px",
                2 => "24px",
                3 => "20px",
                4 => "18px",
                5 => "16px",
                _ => "14px",
            };

            string marginBottom = level <= 2 ? "20px" : "16px";
            string fontWeight = level <= 3 ? "bold" : "600";

            string style = $"margin: 0 0 {marginBottom} 0; font-family: {_fontFamily}; font-size: {fontSize}; font-weight: {fontWeight}; color: {_textColor}; line-height: 1.3;";

            string tag = $"h{level}";
            _ = sb.AppendLine($"<{tag} style=\"{style}\">{HtmlEncode(heading.Text)}</{tag}>");

            AddToPreview(heading.Text);
        }

        private void ConvertText(TextNode text, StringBuilder sb)
        {
            // Handle embedded DSL content
            if (text.IsEmbeddedDsl && text.ContentAsDsl.HasValue)
            {
                DslNode? embeddedNode = DslSerializer.DeserializeFromElement(text.ContentAsDsl.Value);
                if (embeddedNode != null)
                {
                    ConvertNode(embeddedNode, sb, 0);
                    return;
                }
            }

            string contentText = text.ContentAsString ?? "";
            string pStyle = $"margin: 0 0 16px 0; font-family: {_fontFamily}; font-size: 16px; color: {_textColor}; line-height: 1.6;";
            string listStyle = $"margin: 0 0 16px 0; padding-left: 20px; font-family: {_fontFamily}; font-size: 16px; color: {_textColor}; line-height: 1.6;";
            string thStyle = $"padding: 8px 12px; font-family: {_fontFamily}; font-size: 14px; color: {_textColor}; font-weight: bold; border-bottom: 2px solid {_brandColor}; text-align: left;";
            string tdStyle = $"padding: 8px 12px; font-family: {_fontFamily}; font-size: 14px; color: {_textColor}; border-bottom: 1px solid #E5E7EB;";

            foreach (ContentBlock block in MarkdownBlockParser.Parse(contentText))
            {
                switch (block)
                {
                    case HeadingBlock h:
                        int hLevel = Math.Clamp(h.Level, 1, 6);
                        string hFontSize = hLevel switch
                        {
                            1 => "28px",
                            2 => "24px",
                            3 => "20px",
                            4 => "18px",
                            5 => "16px",
                            _ => "14px",
                        };
                        string hStyle = $"margin: 0 0 16px 0; font-family: {_fontFamily}; font-size: {hFontSize}; font-weight: bold; color: {_textColor}; line-height: 1.3;";
                        _ = sb.AppendLine($"<h{hLevel} style=\"{hStyle}\">{ConvertMarkdownInlineToHtml(h.Text)}</h{hLevel}>");
                        AddToPreview(h.Text);
                        break;

                    case ParagraphBlock p:
                        _ = sb.AppendLine($"<p style=\"{pStyle}\">{ConvertMarkdownInlineToHtml(p.Text)}</p>");
                        AddToPreview(p.Text);
                        break;

                    case ListBlock list:
                        string listTag = list.Ordered ? "ol" : "ul";
                        _ = sb.AppendLine($"<{listTag} style=\"{listStyle}\">");
                        AppendEmailListItems(sb, list.Items, listTag, listStyle);
                        foreach (ListItem item in list.Items)
                            AddToPreview(item.Text);
                        _ = sb.AppendLine($"</{listTag}>");
                        break;

                    case TableBlock table:
                        _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin: 0 0 16px 0; border-collapse: collapse;\">");
                        _ = sb.Append("<tr>");
                        foreach (string header in table.Headers)
                            _ = sb.Append($"<th style=\"{thStyle}\">{ConvertMarkdownInlineToHtml(header)}</th>");
                        _ = sb.AppendLine("</tr>");
                        foreach (IReadOnlyList<string> row in table.Rows)
                        {
                            _ = sb.Append("<tr>");
                            foreach (string cell in row)
                                _ = sb.Append($"<td style=\"{tdStyle}\">{ConvertMarkdownInlineToHtml(cell)}</td>");
                            _ = sb.AppendLine("</tr>");
                        }
                        _ = sb.AppendLine("</table>");
                        break;
                }
            }
        }

        private void AppendEmailListItems(StringBuilder sb, IReadOnlyList<ListItem> items, string tag, string listStyle)
        {
            foreach (ListItem item in items)
            {
                _ = sb.Append($"  <li style=\"margin-bottom: 8px;\">{ConvertMarkdownInlineToHtml(item.Text)}");
                if (item.Children.Count > 0)
                {
                    _ = sb.Append($"<{tag} style=\"{listStyle}\">");
                    AppendEmailListItems(sb, item.Children, tag, listStyle);
                    _ = sb.Append($"</{tag}>");
                }
                _ = sb.AppendLine("</li>");
            }
        }

        private void ConvertSection(SectionNode section, StringBuilder sb, int depth)
        {
            // Section spacing
            _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin-bottom: 24px;\">");
            _ = sb.AppendLine("  <tr>");
            _ = sb.AppendLine("    <td>");

            // Section title
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                int level = Math.Clamp(section.Level, 1, 6);
                string fontSize = level == 1 ? "28px" : (level == 2 ? "24px" : "20px");
                string style = $"margin: 0 0 20px 0; font-family: {_fontFamily}; font-size: {fontSize}; font-weight: bold; color: {_textColor}; line-height: 1.3;";

                string tag = $"h{level}";
                _ = sb.AppendLine($"<{tag} style=\"{style}\">{HtmlEncode(section.Title)}</{tag}>");
            }

            // Children
            foreach (DslNode child in section.Children)
            {
                ConvertNode(child, sb, depth + 1);
            }

            _ = sb.AppendLine("    </td>");
            _ = sb.AppendLine("  </tr>");
            _ = sb.AppendLine("</table>");
        }

        private void ConvertContainer(ContainerNode container, StringBuilder sb, int depth)
        {
            // Container as table row
            _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\">");
            _ = sb.AppendLine("  <tr>");
            _ = sb.AppendLine("    <td>");

            foreach (DslNode child in container.Children)
            {
                ConvertNode(child, sb, depth + 1);
            }

            _ = sb.AppendLine("    </td>");
            _ = sb.AppendLine("  </tr>");
            _ = sb.AppendLine("</table>");
        }

        private void ConvertBlock(BlockNode block, StringBuilder sb, int depth)
        {
            string bgColor = block.Variant.Contains("accent", StringComparison.OrdinalIgnoreCase)
                ? _brandColor + "1A" // 10% opacity
                : "#F9FAFB";

            string borderColor = block.Variant.Contains("accent", StringComparison.OrdinalIgnoreCase) ? _brandColor : "#E5E7EB";

            // Block as table with background
            _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin-bottom: 20px;\">");
            _ = sb.AppendLine("  <tr>");
            _ = sb.AppendLine($"    <td style=\"background-color: {bgColor}; border-left: 4px solid {borderColor}; padding: 16px; border-radius: 4px;\">");

            foreach (DslNode child in block.Children)
            {
                ConvertNode(child, sb, depth + 1);
            }

            _ = sb.AppendLine("    </td>");
            _ = sb.AppendLine("  </tr>");
            _ = sb.AppendLine("</table>");
        }

        private void ConvertList(ListNode list, StringBuilder sb)
        {
            string tag = list.Ordered ? "ol" : "ul";
            string style = $"margin: 0 0 16px 0; padding-left: 20px; font-family: {_fontFamily}; font-size: 16px; color: {_textColor}; line-height: 1.6;";

            _ = sb.AppendLine($"<{tag} style=\"{style}\">");

            foreach (string item in list.Items)
            {
                string htmlContent = ConvertMarkdownInlineToHtml(item);
                _ = sb.AppendLine($"  <li style=\"margin-bottom: 8px;\">{htmlContent}</li>");
                AddToPreview(item);
            }

            _ = sb.AppendLine($"</{tag}>");
        }

        private void ConvertImage(ImageNode image, StringBuilder sb)
        {
            string alt = !string.IsNullOrWhiteSpace(image.Alt) ? HtmlEncode(image.Alt) : "Image";

            // Image wrapped in table for Outlook compatibility
            _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin-bottom: 20px;\">");
            _ = sb.AppendLine("  <tr>");
            _ = sb.AppendLine("    <td align=\"center\">");
            _ = sb.AppendLine($"      <img src=\"{HtmlEncode(image.Ref)}\" alt=\"{alt}\" style=\"max-width: 100%; height: auto; display: block; border: 0;\" />");
            _ = sb.AppendLine("    </td>");
            _ = sb.AppendLine("  </tr>");
            _ = sb.AppendLine("</table>");
        }

        private void ConvertButton(ButtonNode button, StringBuilder sb)
        {
            string variant = button.Variant.ToLowerInvariant();
            string bgColor = variant switch
            {
                "secondary" => "#6B7280",
                "outline" => "#FFFFFF",
                _ => _brandColor,
            };

            string textColor = variant == "outline" ? _brandColor : "#FFFFFF";
            string borderColor = variant == "outline" ? _brandColor : bgColor;

            // Button as table (best email compatibility)
            _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin: 20px 0;\">");
            _ = sb.AppendLine("  <tr>");
            _ = sb.AppendLine($"    <td align=\"center\" style=\"border-radius: 4px; background-color: {bgColor}; border: 2px solid {borderColor};\">");
            _ = sb.AppendLine($"      <a href=\"{HtmlEncode(button.Url)}\" style=\"display: inline-block; padding: 12px 24px; font-family: {_fontFamily}; font-size: 16px; font-weight: bold; color: {textColor}; text-decoration: none;\">{HtmlEncode(button.Label)}</a>");
            _ = sb.AppendLine("    </td>");
            _ = sb.AppendLine("  </tr>");
            _ = sb.AppendLine("</table>");
        }

        private void ConvertSpacer(SpacerNode spacer, StringBuilder sb)
        {
            _ = sb.AppendLine($"<div style=\"height: {spacer.Size}px; line-height: {spacer.Size}px; font-size: {spacer.Size}px;\">&nbsp;</div>");
        }

        private void ConvertDivider(DividerNode divider, StringBuilder sb)
        {
            _ = sb.AppendLine("<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin: 24px 0;\">");
            _ = sb.AppendLine("  <tr>");
            _ = sb.AppendLine("    <td style=\"border-top: 1px solid #E5E7EB;\"></td>");
            _ = sb.AppendLine("  </tr>");
            _ = sb.AppendLine("</table>");
        }

        private void AddToPreview(string text)
        {
            if (_previewTextLength >= 100)
                return;

            string cleanText = StripHtml(text);
            int available = 100 - _previewTextLength;

            if (cleanText.Length <= available)
            {
                _ = _previewTextBuilder.Append(cleanText);
                _ = _previewTextBuilder.Append(' ');
                _previewTextLength += cleanText.Length + 1;
            }
            else
            {
                _ = _previewTextBuilder.Append(cleanText.Substring(0, available));
                _previewTextLength = 100;
            }
        }

        private static string StripHtml(string text)
        {
            // Simple HTML stripping
            return Regex.Replace(text, "<.*?>", string.Empty);
        }

        private string ConvertMarkdownInlineToHtml(string text)
        {
            // Use shared inline converter, then add email-specific inline styles
            string html = MarkdownInlineConverter.ToHtml(text);
            html = html.Replace("<a href=", $"<a style=\"color: {_brandColor}; text-decoration: underline;\" href=");
            html = html.Replace("<strong>", "<strong style=\"font-weight: bold;\">");
            html = html.Replace("<em>", "<em style=\"font-style: italic;\">");
            return html;
        }

        private static string HtmlEncode(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? "" : MarkdownInlineConverter.HtmlEncode(text);
        }
    }
}
