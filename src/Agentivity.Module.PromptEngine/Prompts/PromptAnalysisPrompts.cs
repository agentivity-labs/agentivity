using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Services;

namespace Agentivity.Module.PromptEngine.Prompts;

public static class PromptAnalysisPrompts
{
    private const string ValidResponseSchemaExample = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "priority": { "type": "string" }
                },
                "required": ["name", "priority"],
                "additionalProperties": false
              }
            }
          },
          "required": ["title", "items"],
          "additionalProperties": false
        }
        """;

    public const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "overall_score": { "type": "number" },
            "dimension_scores": {
              "type": "object",
              "properties": {
                "clarity": { "type": "number" },
                "specificity": { "type": "number" },
                "structure": { "type": "number" },
                "completeness": { "type": "number" },
                "efficiency": { "type": "number" }
              },
              "required": ["clarity", "specificity", "structure", "completeness", "efficiency"],
              "additionalProperties": false
            },
            "issues": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "category": { "type": "string" },
                  "severity": { "type": "string", "enum": ["low", "medium", "high"] },
                  "description": { "type": "string", "maxLength": 320 },
                  "suggestion": { "type": "string", "maxLength": 320 }
                },
                "required": ["category", "severity", "description", "suggestion"],
                "additionalProperties": false
              }
            },
            "strengths": {
              "type": "array",
              "maxItems": 5,
              "items": { "type": "string", "maxLength": 240 }
            },
            "recommendations": {
              "type": "array",
              "maxItems": 5,
              "items": { "type": "string", "maxLength": 320 }
            },
            "improved_components": {
              "type": "object",
              "properties": {
                "user_prompt": { "type": ["string", "null"] },
                "system_prompt": { "type": ["string", "null"] },
                "response_schema": {
                  "type": ["object", "null"],
                  "additionalProperties": true
                }
              },
              "required": ["user_prompt", "system_prompt", "response_schema"],
              "additionalProperties": false
            }
          },
          "required": ["overall_score", "dimension_scores", "issues", "strengths", "recommendations", "improved_components"],
          "additionalProperties": false
        }
        """;

    public static string BuildAnalysisPrompt(PromptEntity PromptEntity)
    {
        var requiredPlaceholders = PromptPlaceholderGuardrails.GetRequiredPlaceholders(PromptEntity);

        return $$"""
        <role>
        You are an expert prompt engineer. Analyze the provided PromptEntity and return a structured quality assessment.
        </role>

        <objective>
        Score the PromptEntity from 0 to 10. Identify issues, strengths, and actionable recommendations.
        Propose improvements across the 3 editable PromptEntity components: user PromptEntity, system PromptEntity, and response schema.
        Return explicit dimension scores so a UI can render a radar chart.
        Preserve every part that is already strong.
        Only propose a modification when it is materially better than the current version.
        Do not simplify away useful constraints, structure, or specificity.
        If one component does not need changes, keep it very close to the current version, or return null only when the component is genuinely not applicable.
        </objective>

        <scoring_dimensions>
        Evaluate across these dimensions using PromptEntity-engineering best practices:
        - Clarity: Is the intent unambiguous?
        - Specificity: Are constraints, format, and expectations explicit?
        - Structure: Is the PromptEntity well-organized with clear sections?
        - Completeness: Does it cover edge cases and provide enough context?
        - Efficiency: Is it concise without losing necessary detail?
        </scoring_dimensions>

        <best_practices_reference>
        Base your evaluation on widely accepted PromptEntity-engineering best practices:
        - explicit objective and success criteria
        - separation of user PromptEntity, system PromptEntity, and output contract responsibilities
        - clear constraints and formatting expectations
        - structured instructions, priorities, and boundaries
        - enough context to reduce ambiguity without unnecessary verbosity
        </best_practices_reference>

        {{(requiredPlaceholders.Count == 0 ? string.Empty : $$"""
        <critical_placeholders>
        Preserve these placeholders exactly in any improved user PromptEntity. Never remove them, rename them, or replace them with concrete values:
        {{string.Join("\n", requiredPlaceholders.Select(placeholder => $"- {placeholder}"))}}
        </critical_placeholders>
        """)}}

        <component_quality_rules>
        - `user_prompt`: keep it task-focused, explicit, and rich enough in constraints, examples, and expected behavior.
        - `system_prompt`: prefer a more structured formulation when useful, with clear role, boundaries, priorities, and output behavior.
        - `response_schema`: keep it valid, explicit, minimal, and aligned with the intended output contract.
        - `response_schema` must be a real JSON Schema object, not an example response object and not prose describing a format.
        - if you propose `response_schema`, return it as a native JSON object, not as an escaped JSON string.
        - the schema object must use JSON Schema keywords such as `type`, `properties`, `items`, `required`, or `additionalProperties` when relevant.
        - Never degrade a component just to make it shorter or more generic.
        - Prefer a stronger, more structured `system_prompt` when the current one is weak or absent.
        </component_quality_rules>

        <input>
        Prompt content:
        {{PromptEntity.Content}}

        System PromptEntity:
        {{PromptEntity.SystemPrompt ?? "<none>"}}

        Response schema:
        {{PromptEntity.ResponseSchema ?? "<none>"}}
        </input>

        <output_requirements>
        `dimension_scores` must contain numeric scores from 0 to 10 for clarity, specificity, structure, completeness, and efficiency.
        `overall_score` must be coherent with the dimension scores.
        `improved_components.user_prompt` targets the main user PromptEntity body.
        `improved_components.system_prompt` targets system-level instructions.
        `improved_components.response_schema` targets the structured output contract and must be a valid JSON Schema object when present.
        Keep the proposal coherent across the three components.
        The proposed system PromptEntity should usually be more structured than the user PromptEntity.
        If no structured output is appropriate, return null for `improved_components.response_schema`.
        </output_requirements>

        <response_schema_example>
        Example of a valid JSON Schema for a structured output:
        {{ValidResponseSchemaExample}}
        Do not return an example payload object with business placeholders like title equals string and items equals an example array.
        </response_schema_example>

        Return valid JSON only.
        """;
    }
}
