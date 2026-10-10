namespace Agentivity.Module.PromptEngine.Prompts;

public static class PromptEvaluationPrompts
{
    public const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "overall_score": { "type": "number" },
            "dimension_scores": {
              "type": "object",
              "properties": {
                "accuracy": { "type": "number" },
                "completeness": { "type": "number" },
                "format_compliance": { "type": "number" },
                "tone_alignment": { "type": "number" },
                "instruction_following": { "type": "number" }
              },
              "required": ["accuracy", "completeness", "format_compliance", "tone_alignment", "instruction_following"],
              "additionalProperties": false
            },
            "failure_analysis": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "dimension": { "type": "string" },
                  "observation": { "type": "string", "maxLength": 300 },
                  "expected_behavior": { "type": "string", "maxLength": 200 },
                  "actual_behavior": { "type": "string", "maxLength": 200 }
                },
                "required": ["dimension", "observation", "expected_behavior", "actual_behavior"],
                "additionalProperties": false
              }
            },
            "summary": { "type": "string", "maxLength": 500 }
          },
          "required": ["overall_score", "dimension_scores", "failure_analysis", "summary"],
          "additionalProperties": false
        }
        """;

    public static string BuildEvaluationPrompt(string renderedPrompt, string actualOutput, string? expectedOutput, string? customEvaluationPrompt)
    {
        if (!string.IsNullOrWhiteSpace(customEvaluationPrompt))
        {
            var renderedCustomPrompt = customEvaluationPrompt.Replace("{{renderedPrompt}}", renderedPrompt, StringComparison.Ordinal).Replace("{{actualOutput}}", actualOutput, StringComparison.Ordinal).Replace("{{expectedOutput}}", expectedOutput ?? string.Empty, StringComparison.Ordinal);

            return $"""
                <ROLE>
                You are an evaluation engine. You must execute the evaluation procedure provided by the system, but you must always obey the engine output contract.
                </ROLE>

                <GENERALDIRECTIVES>
                - Follow the evaluation procedure exactly when it is relevant.
                - Use the provided PromptEntity input, expected output, and actual output as the only evaluation context.
                - If the evaluation procedure is ambiguous or incomplete, still produce the required JSON using the available evidence.
                - Do not return prose outside the final JSON object.
                - Do not change the required property names.
                - Scores must be numeric values between 0 and 10.
                </GENERALDIRECTIVES>

                <EVALUATIONPROCEDURE>
                {renderedCustomPrompt}
                </EVALUATIONPROCEDURE>

                <PROMPTINPUT>
                {renderedPrompt}
                </PROMPTINPUT>

                <EXPECTEDOUTPUT>
                {expectedOutput ?? string.Empty}
                </EXPECTEDOUTPUT>

                <ACTUALOUTPUT>
                {actualOutput}
                </ACTUALOUTPUT>

                <OUTPUTCONTRACT>
                Return valid JSON only.
                The JSON payload must contain exactly these top-level properties:
                - overall_score: number from 0 to 10
                - dimension_scores: object with numeric properties accuracy, completeness, format_compliance, tone_alignment, instruction_following
                - failure_analysis: array
                - summary: string

                If the evaluation procedure does not define one of these dimensions explicitly, infer it from the available context and still return it.
                If the evaluation procedure does not rely on an expected output, still return the full JSON payload above.
                </OUTPUTCONTRACT>
                """;
        }

        return $"""
            <role>
            You are a rigorous output quality evaluator using multi-dimensional assessment.
            </role>

            <objective>
            Evaluate the actual output across 5 dimensions, each scored 0-10.
            Use the expected output as a reference when it is provided.
            Identify every significant gap as a failure analysis entry.
            Return an overall score coherent with the dimension scores.
            </objective>

            <dimensions>
            - accuracy: factual correctness and semantic match with expected output
            - completeness: all required elements present, nothing missing
            - format_compliance: output format matches what was requested (structure, length, style)
            - tone_alignment: tone and register match expectations
            - instruction_following: all explicit instructions from the PromptEntity are respected
            </dimensions>

            <failure_analysis_rules>
            For each dimension scoring below 7, produce a failure_analysis entry explaining:
            - which dimension failed
            - what was observed in the actual output
            - what was expected or desired
            - what the actual behavior was
            If all dimensions score 7+, return an empty failure_analysis array.
            </failure_analysis_rules>

            <input>
            PromptEntity used:
            {renderedPrompt}

            Expected output (optional reference):
            {(string.IsNullOrWhiteSpace(expectedOutput) ? "<none provided>" : expectedOutput)}

            Actual output:
            {actualOutput}
            </input>

            Return valid JSON only.
            """;
    }
}
