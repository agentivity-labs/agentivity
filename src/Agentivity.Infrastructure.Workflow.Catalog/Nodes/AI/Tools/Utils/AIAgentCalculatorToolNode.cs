using Agentivity.Domain.Workflow;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Utils;

/// <summary>
/// Structured AI-agent callable calculator tool.
/// Supports basic arithmetic expressions with parentheses and unary operators.
/// </summary>
public sealed class AIAgentCalculatorToolNode : ResourceNode, IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ToolName = "calculate_expression";

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "expression": {
      "type": "string",
      "description": "Arithmetic expression using plain decimal numbers and operators +, -, *, /, ** (power), and parentheses. No scientific notation, no letter suffixes, no variable names."
    }
  },
  "required": ["expression"],
  "additionalProperties": false
}
""";

    public AIAgentCalculatorToolNode()
        : base("ai.tools.utils.calculator")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Calculator Tool", "Evaluates arithmetic expressions for AI agent tool calling.", "ai.tools.utils", whenToUse: "To let an agent get exact results for plain arithmetic (totals, percentages, prices with tax) instead of working them out itself. Not for longer logic or reshaping data: use the JavaScript Tool instead.");
        DefineEffects(NodeEffects.None);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Calculator Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([
            LlmToolDefinitionJson.Create(
                ToolName,
                "Evaluate an arithmetic expression. Supported operators: +, -, *, / (basic arithmetic), ** (power), and parentheses. Numbers must be plain decimals only (e.g. 1500, 1.1, 0.15) — no scientific notation (1e3), no suffixes (1500b), no letters anywhere.",
                InputSchema,
                allowToolStateReuse: true
            ),
        ]);
    }

    public ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tool '{toolName}' is not exposed by this node.");
        }

        if (!arguments.TryGetProperty("expression", out JsonElement expressionElement) || expressionElement.ValueKind != JsonValueKind.String)
        {
            return ValueTask.FromResult(McpToolResultJson.CreateFailedResult("The calculator tool requires a string 'expression' argument.", "invalid_arguments"));
        }

        string? expression = expressionElement.GetString();
        if (string.IsNullOrWhiteSpace(expression))
        {
            return ValueTask.FromResult(McpToolResultJson.CreateFailedResult("The calculator expression cannot be empty.", "invalid_arguments"));
        }

        string? unsupportedOperator = DetectUnsupportedOperator(expression);
        if (unsupportedOperator is not null)
        {
            return ValueTask.FromResult(
                McpToolResultJson.CreateFailedResult(
                    $"The expression contains the unsupported operator '{unsupportedOperator}'. "
                        + $"Only +, -, *, / and parentheses are supported. "
                        + $"Rewrite the expression using only these operators "
                        + $"(e.g. use repeated multiplication instead of ^ for powers: 1.04*1.04*1.04 instead of 1.04^3).",
                    "unsupported_operator"
                )
            );
        }

        try
        {
            var parser = new ExpressionParser(expression);
            double result = parser.Parse();

            var payload = new
            {
                expression,
                result,
                formatted_result = result.ToString("G17", CultureInfo.InvariantCulture),
            };

            return ValueTask.FromResult(McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), false));
        }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException)
        {
            return ValueTask.FromResult(
                McpToolResultJson.CreateFailedResult($"The expression '{expression}' could not be evaluated: {ex.Message} " + $"Only +, -, *, / and parentheses are supported. Rewrite the expression and try again.", ex is DivideByZeroException ? "division_by_zero" : "invalid_expression")
            );
        }
    }

    /// <summary>
    /// Scans the expression for operator characters that are syntactically invalid
    /// (not +, -, *, /, digits, dot, whitespace, or parentheses).
    /// Returns the first unsupported character found, or null if the expression looks valid.
    /// Detection happens before parsing so the error message is actionable.
    /// </summary>
    private static string? DetectUnsupportedOperator(string expression)
    {
        foreach (char ch in expression)
        {
            if (char.IsDigit(ch) || char.IsWhiteSpace(ch))
                continue;

            if (ch is '+' or '-' or '*' or '/' or '.' or '(' or ')')
                continue;

            return ch.ToString();
        }

        return null;
    }

    private sealed class ExpressionParser
    {
        private readonly string _expression;
        private int _index;

        public ExpressionParser(string expression)
        {
            _expression = expression ?? throw new ArgumentNullException(nameof(expression));
        }

        public double Parse()
        {
            double value = ParseExpression();
            SkipWhitespace();

            return _index < _expression.Length ? throw new FormatException($"Unexpected token at position {_index + 1}.") : value;
        }

        private double ParseExpression()
        {
            double value = ParseTerm();

            while (true)
            {
                SkipWhitespace();
                if (TryConsume('+'))
                {
                    value += ParseTerm();
                    continue;
                }

                if (TryConsume('-'))
                {
                    value -= ParseTerm();
                    continue;
                }

                return value;
            }
        }

        private double ParseTerm()
        {
            double value = ParsePower();

            while (true)
            {
                SkipWhitespace();
                // Consume ** before *, so power is handled at ParsePower level.
                if (_index + 1 < _expression.Length && _expression[_index] == '*' && _expression[_index + 1] == '*')
                    return value;

                if (TryConsume('*'))
                {
                    value *= ParsePower();
                    continue;
                }

                if (TryConsume('/'))
                {
                    double divisor = ParsePower();
                    if (Math.Abs(divisor) < double.Epsilon)
                        throw new DivideByZeroException("Division by zero is not allowed.");

                    value /= divisor;
                    continue;
                }

                return value;
            }
        }

        // Right-associative: a**b**c = a**(b**c)
        private double ParsePower()
        {
            double base_ = ParseFactor();
            SkipWhitespace();
            if (_index + 1 < _expression.Length && _expression[_index] == '*' && _expression[_index + 1] == '*')
            {
                _index += 2;
                double exp = ParsePower();
                return Math.Pow(base_, exp);
            }

            return base_;
        }

        private double ParseFactor()
        {
            SkipWhitespace();

            if (TryConsume('+'))
            {
                return ParseFactor();
            }

            if (TryConsume('-'))
            {
                return -ParseFactor();
            }

            if (TryConsume('('))
            {
                double value = ParseExpression();
                SkipWhitespace();
                return !TryConsume(')') ? throw new FormatException("Missing closing parenthesis.") : value;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            int start = _index;

            while (_index < _expression.Length)
            {
                char current = _expression[_index];
                if (char.IsDigit(current) || current == '.')
                {
                    _index++;
                    continue;
                }

                break;
            }

            if (start == _index)
            {
                throw new FormatException($"Expected a number at position {_index + 1}.");
            }

            string token = _expression[start.._index];
            return !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? throw new FormatException($"'{token}' is not a valid number.") : value;
        }

        private bool TryConsume(char expected)
        {
            SkipWhitespace();
            if (_index >= _expression.Length || _expression[_index] != expected)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _expression.Length && char.IsWhiteSpace(_expression[_index]))
            {
                _index++;
            }
        }
    }
}
