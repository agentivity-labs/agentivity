using Agentivity.Infrastructure.AgUi;

namespace Agentivity.Host.AgUi;

/// <summary>
/// Platform-managed AG-UI widget bundle definitions.
/// Each bundle is registered into <see cref="IAgUiWidgetBundleRegistry"/> at startup.
/// </summary>
public static class AgUiWidgetCatalog
{
    /// <summary>
    /// The <c>agentivity_artifacts</c> bundle (a stable protocol id) — the built-in widgets shipped
    /// with the Flutter and React SDKs.
    /// </summary>
    public static AgUiWidgetBundle ArtifactsBundle { get; } =
        new(
            Id: "agentivity_artifacts",
            DisplayName: "Agentivity Artifacts",
            Version: "1.0.0",
            Description: "General-purpose artifact widgets: charts, data tables, code viewers, status indicators, math expressions and SVG.",
            Widgets:
            [
                // Charts
                new("BarChart", "charts", "Vertical or horizontal bar chart — use to compare amounts across a few categories", BarChartSchema),
                new("LineChart", "charts", "Line / time-series chart — use for a value that evolves over time or along an ordered axis", LineOrAreaChartSchema),
                new("PieChart", "charts", "Pie / donut chart — use for the share of each part in a whole, with a handful of parts", PieChartSchema),
                new("AreaChart", "charts", "Filled area chart — use for cumulative or stacked values over time", LineOrAreaChartSchema),
                new("RadarChart", "charts", "Spider / radar chart — use to compare items across several criteria on the same scale", RadarChartSchema),
                // Data
                new("MetricCard", "data", "Single KPI metric card with label and value — use to highlight one key number, optionally with its trend", MetricCardSchema),
                new("StatGrid", "data", "Grid of multiple stat cells — use to show several key figures together at a glance", StatGridSchema),
                new("KeyValue", "data", "Simple key-value property list — use for the details of one item (a record, an order, a profile)", KeyValueSchema),
                // Code
                new("CodeBlock", "code", "Syntax-highlighted code block — use to show code, a query or a configuration to copy", CodeBlockSchema),
                new("JsonViewer", "code", "Collapsible JSON tree viewer — use to inspect structured data that has no better layout", JsonViewerSchema),
                // Status
                new("StatusCard", "status", "Status badge with icon and message — use to report the outcome of an action (success, warning, error)", StatusCardSchema),
                new("Timeline", "status", "Vertical event timeline — use for a sequence of dated events or steps", TimelineSchema),
                // Math
                new("Latex", "math", "LaTeX / KaTeX mathematical expression — use to display a formula", LatexSchema),
                // SVG
                new("Svg", "svg", "Inline SVG image — use for a simple diagram or illustration you can draw as vector shapes", SvgSchema),
                // Media
                new("ImageGallery", "media", "Grid of pictures with captions and links — use to show products, places or any visual results; copy picture addresses from the source, never invent them", ImageGallerySchema),
                // Interaction
                new("QuestionForm", "interaction", "Multi-question form with labeled text fields — use when asking several questions at once", QuestionFormSchema, RequiresHumanResponse: true),
                new("ChoiceCard", "interaction", "Single or multi-select option list — use for categorisation or type selection", ChoiceCardSchema, RequiresHumanResponse: true),
                new("ConfirmCard", "interaction", "Confirm / cancel card with optional context block — use before irreversible actions", ConfirmCardSchema, RequiresHumanResponse: true),
                new("RatingCard", "interaction", "Star or numbered scale rating — use to collect satisfaction or quality feedback", RatingCardSchema, RequiresHumanResponse: true),
                new("DatePickerCard", "interaction", "Date or date-range picker — use to collect scheduling or delivery dates", DatePickerCardSchema, RequiresHumanResponse: true),
                new("SummaryCard", "interaction", "Structured recap with key-value sections — use before a confirmation step to show what is about to happen, no submit", SummaryCardSchema, RequiresHumanResponse: false),
                new("SourceInput", "interaction", "Document intake — the human uploads a file, gives a link, or pastes text. The widget only collects: the reply is a small JSON envelope {kind:'file', fileId, name} | {kind:'url', url} | {kind:'text', content}, to be read by the workflow (extract a file by fileId, scrape a url, or use the text as is) — use to ask for a CV, contract, report or any source material", SourceInputSchema, RequiresHumanResponse: true),
            ]
        );

    /// <summary>Returns all platform bundles to be registered at startup.</summary>
    public static IEnumerable<AgUiWidgetBundle> AllBundles()
    {
        yield return ArtifactsBundle;
    }

    // ── Display-only widget schemas ──────────────────────────────────────────
    // Every catalog widget carries a schema, not just the "interaction" (human-
    // response) ones below — a display-only widget offered with nothing but a
    // one-line description let an agent guess a plausible-but-wrong prop shape
    // (e.g. KeyValue's `items` as a flat object instead of an array of
    // {key,value} pairs), rendering an empty shell. Each mirrors exactly the
    // corresponding React/Flutter component's props — no translation layer.

    private const string BarChartSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "labels": { "type": "array", "items": { "type": "string" } },
            "datasets": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "data": { "type": "array", "items": { "type": "number" } },
                  "color": { "type": "string", "description": "CSS color, e.g. '#2563eb'." }
                },
                "required": ["label", "data"],
                "additionalProperties": false
              }
            },
            "height": { "type": "number" }
          },
          "required": ["title", "labels", "datasets"],
          "additionalProperties": false
        }
        """;

    private const string LineOrAreaChartSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "labels": { "type": "array", "items": { "type": "string" } },
            "datasets": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "data": { "type": "array", "items": { "type": "number" } },
                  "color": { "type": "string", "description": "CSS color, e.g. '#2563eb'." },
                  "smooth": { "type": "boolean" },
                  "fill": { "type": "boolean" }
                },
                "required": ["label", "data"],
                "additionalProperties": false
              }
            },
            "height": { "type": "number" }
          },
          "required": ["title", "labels", "datasets"],
          "additionalProperties": false
        }
        """;

    private const string PieChartSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "sections": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "value": { "type": "number" },
                  "color": { "type": "string", "description": "CSS color, e.g. '#2563eb'." }
                },
                "required": ["label", "value"],
                "additionalProperties": false
              }
            },
            "donut": { "type": "boolean" },
            "height": { "type": "number" }
          },
          "required": ["title", "sections"],
          "additionalProperties": false
        }
        """;

    private const string RadarChartSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "labels": { "type": "array", "items": { "type": "string" } },
            "datasets": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "data": { "type": "array", "items": { "type": "number" } },
                  "color": { "type": "string", "description": "CSS color, e.g. '#2563eb'." }
                },
                "required": ["label", "data"],
                "additionalProperties": false
              }
            },
            "max": { "type": "number", "description": "Fixed axis maximum; omit to auto-scale to the data." },
            "height": { "type": "number" }
          },
          "required": ["title", "labels", "datasets"],
          "additionalProperties": false
        }
        """;

    private const string MetricCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "value": { "type": "string", "description": "The headline value, e.g. '1 240 EUR' or '87%'." },
            "delta": { "type": "string", "description": "Short change indicator shown next to the value, e.g. '+12%'." },
            "trend": { "type": "string", "enum": ["up", "down", "flat"] },
            "subtitle": { "type": "string" }
          },
          "required": ["title", "value"],
          "additionalProperties": false
        }
        """;

    private const string StatGridSchema = """
        {
          "type": "object",
          "properties": {
            "columns": { "type": "integer", "minimum": 1, "description": "Caps how many columns the grid uses; omit to auto-fit." },
            "metrics": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "value": { "type": "string" },
                  "delta": { "type": "string" },
                  "trend": { "type": "string", "enum": ["up", "down", "flat"] },
                  "subtitle": { "type": "string" }
                },
                "required": ["title", "value"],
                "additionalProperties": false
              }
            }
          },
          "required": ["metrics"],
          "additionalProperties": false
        }
        """;

    private const string KeyValueSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "items": {
              "type": "array",
              "minItems": 1,
              "description": "Each row of the property list, in display order.",
              "items": {
                "type": "object",
                "properties": {
                  "key": { "type": "string" },
                  "value": { "type": "string" },
                  "highlight": { "type": "boolean", "description": "Renders the value in the accent color, e.g. for a total." }
                },
                "required": ["key", "value"],
                "additionalProperties": false
              }
            },
            "dividers": { "type": "boolean", "description": "Shows a divider line between rows. Defaults to true." }
          },
          "required": ["title", "items"],
          "additionalProperties": false
        }
        """;

    private const string CodeBlockSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "language": { "type": "string", "description": "highlight.js language id, e.g. 'json', 'python', 'sql'." },
            "code": { "type": "string" },
            "maxLines": { "type": "integer", "description": "Scroll height cap in lines. Defaults to 40." }
          },
          "required": ["title", "language", "code"],
          "additionalProperties": false
        }
        """;

    private const string JsonViewerSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "data": { "description": "Any JSON-serializable value (object, array, or scalar) to render as a collapsible tree." },
            "expanded": { "type": "boolean", "description": "Whether object/array nodes start open. Defaults to true." }
          },
          "required": ["title", "data"],
          "additionalProperties": false
        }
        """;

    private const string StatusCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "status": { "type": "string", "enum": ["success", "warning", "error", "info"] },
            "message": { "type": "string" },
            "details": { "type": "array", "items": { "type": "string" }, "description": "Optional bullet points shown below the message." }
          },
          "required": ["title", "message"],
          "additionalProperties": false
        }
        """;

    private const string TimelineSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "events": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "time": { "type": "string" },
                  "status": { "type": "string", "enum": ["success", "error", "warning", "running"], "description": "Omit for a plain neutral dot." },
                  "note": { "type": "string" }
                },
                "required": ["label", "time"],
                "additionalProperties": false
              }
            }
          },
          "required": ["title", "events"],
          "additionalProperties": false
        }
        """;

    private const string LatexSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "tex": { "type": "string", "description": "KaTeX-compatible LaTeX source." },
            "display": { "type": "boolean", "description": "true (default) renders a centered display block; false renders inline-sized." }
          },
          "required": ["title", "tex"],
          "additionalProperties": false
        }
        """;

    private const string SvgSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "svg": { "type": "string", "description": "A full <svg>...</svg> markup string." },
            "height": { "type": "number" },
            "fit": { "type": "string", "enum": ["contain", "cover", "fill", "none", "fitWidth", "fitHeight"] }
          },
          "required": ["title", "svg"],
          "additionalProperties": false
        }
        """;

    // ── Interaction widget schemas ──────────────────────────────────────────
    // Each schema mirrors exactly the props the corresponding Flutter widget
    // (the SDKs' interaction widgets) expects — no translation
    // layer between what the LLM generates and what gets rendered.

    private const string QuestionFormSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "Form title." },
            "questions": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "label": { "type": "string", "description": "The question text shown above the field." },
                  "hint": { "type": "string" },
                  "required": { "type": "boolean" },
                  "type": { "type": "string", "enum": ["text", "date", "number", "boolean"], "description": "The field this question renders as. Default 'text' (a free-text box). Use 'date' for ANY question about a date — a native date picker, never free text; 'number' for a quantity, an age, a count; 'boolean' for a yes/no question (rendered as two buttons)." }
                },
                "required": ["id", "label"],
                "additionalProperties": false
              }
            },
            "submitLabel": { "type": "string" }
          },
          "required": ["title", "questions"],
          "additionalProperties": false
        }
        """;

    private const string ChoiceCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "question": { "type": "string" },
            "multiple": { "type": "boolean", "description": "Whether the human may select more than one option." },
            "options": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "label": { "type": "string" },
                  "description": { "type": "string" },
                  "imageUrl": { "type": "string", "description": "http(s) address of a picture, copied from the source page — never invented. The widget shows a neutral placeholder if it cannot load." }
                },
                "required": ["id", "label"],
                "additionalProperties": false
              }
            },
            "submitLabel": { "type": "string" }
          },
          "required": ["title", "multiple", "options"],
          "additionalProperties": false
        }
        """;

    private const string ConfirmCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "message": { "type": "string" },
            "context": { "type": "string" },
            "confirmLabel": { "type": "string" },
            "cancelLabel": { "type": "string" }
          },
          "required": ["title", "message"],
          "additionalProperties": false
        }
        """;

    private const string RatingCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "question": { "type": "string" },
            "mode": { "type": "string", "enum": ["stars", "scale"] },
            "max": { "type": "integer" },
            "labels": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["title", "mode", "max"],
          "additionalProperties": false
        }
        """;

    // No minDate/maxDate: LLMs don't reliably know the current date and tend to guess stale
    // year bounds, which can exclude today entirely. The Flutter widget defaults to a sensible
    // window around the current date instead.
    private const string DatePickerCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "question": { "type": "string" },
            "mode": { "type": "string", "enum": ["single", "range"] },
            "submitLabel": { "type": "string" }
          },
          "required": ["title", "mode"],
          "additionalProperties": false
        }
        """;

    private const string SourceInputSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "What is being asked for, e.g. 'Candidate CV'." },
            "description": { "type": "string", "description": "One line telling the human what to provide." },
            "sources": {
              "type": "array",
              "items": { "type": "string", "enum": ["upload", "url", "paste"] },
              "description": "Which ways to provide it, in tab order. Default: all three."
            },
            "accept": { "type": "string", "description": "Comma-separated file extensions accepted for uploads. Default '.pdf,.docx,.txt,.md'." },
            "maxSizeMb": { "type": "number", "description": "Largest accepted upload, in MB. Default 2 (the platform's own limit still applies)." },
            "placeholder": { "type": "string" },
            "submitLabel": { "type": "string" }
          },
          "required": ["title"],
          "additionalProperties": false
        }
        """;

    private const string SummaryCardSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "imageUrl": { "type": "string", "description": "Banner picture shown above all sections (http(s) address)." },
            "imageAlt": { "type": "string" },
            "sections": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "imageUrl": { "type": "string", "description": "http(s) address of a picture, copied from the source page — never invented. The widget shows a neutral placeholder if it cannot load." },
                  "imageAlt": { "type": "string" },
                  "badge": { "type": "string", "description": "Short highlighted tag next to the label, e.g. 'Best value'." },
                  "url": { "type": "string", "description": "http(s) link to the source of this section; makes the label and picture clickable." },
                  "items": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "key": { "type": "string" },
                        "value": { "type": "string" },
                        "highlight": { "type": "boolean" }
                      },
                      "required": ["key", "value"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["label", "items"],
                "additionalProperties": false
              }
            },
            "note": { "type": "string" }
          },
          "required": ["title", "sections"],
          "additionalProperties": false
        }
        """;

    private const string ImageGallerySchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "images": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "url": { "type": "string", "description": "http(s) address of the picture, copied from the source page — never invented." },
                  "alt": { "type": "string" },
                  "caption": { "type": "string" },
                  "href": { "type": "string", "description": "http(s) link opened when the picture is clicked." }
                },
                "required": ["url"],
                "additionalProperties": false
              }
            },
            "columns": { "type": "integer", "minimum": 1, "maximum": 4, "description": "Pictures per row (default 3)." },
            "aspectRatio": { "type": "string", "description": "CSS ratio such as '4 / 3' (default) or '1 / 1'." },
            "fit": { "type": "string", "enum": ["contain", "cover"], "description": "contain shows the whole picture (products); cover crops to fill." }
          },
          "required": ["title", "images"],
          "additionalProperties": false
        }
        """;
}
