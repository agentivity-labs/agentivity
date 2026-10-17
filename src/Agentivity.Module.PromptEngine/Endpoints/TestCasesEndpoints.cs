using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Services;
using Agentivity.Module.PromptEngine.Storage;

namespace Agentivity.Module.PromptEngine.Endpoints;

public static class TestCasesEndpoints
{
    public static void MapTestCasesEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(
            "/{promptId}/testcases",
            async (string promptId, TestCase testCase, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var prompt = await store.GetPromptAsync(promptId, ct);
                if (prompt is null)
                    return Results.NotFound();

                if (string.IsNullOrWhiteSpace(testCase.ExpectedOutput))
                {
                    return PromptEngineEndpointErrors.Validation("test_case_expected_output_required", "A structured test case requires a non-empty expected_output.");
                }

                testCase.Id = Guid.NewGuid().ToString();
                testCase.PromptId = promptId;
                testCase.CreatedAt = DateTime.UtcNow;

                await store.SaveTestCaseAsync(testCase, ct);
                return Results.Created($"{PromptEngineEndpointRouteBuilderExtensions.PromptsRoutePrefix}/testcases/{testCase.Id}", testCase);
            }
        )
            .WithSummary("Add a test case to a prompt");

        group.MapGet(
            "/{promptId}/testcases",
            async (string promptId, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var cases = await store.ListTestCasesAsync(promptId, ct);
                return Results.Ok(cases);
            }
        )
            .WithSummary("List the test cases of a prompt");

        group.MapGet(
            "/testcases/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var testCase = await store.GetTestCaseAsync(id, ct);
                return testCase is not null ? Results.Ok(testCase) : Results.NotFound();
            }
        )
            .WithSummary("Get a test case");

        group.MapPut(
            "/testcases/{id}",
            async (string id, TestCase testCase, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var existingTestCase = await store.GetTestCaseAsync(id, ct);
                if (existingTestCase is null)
                    return Results.NotFound();

                if (string.IsNullOrWhiteSpace(testCase.ExpectedOutput))
                {
                    return PromptEngineEndpointErrors.Validation("test_case_expected_output_required", "A structured test case requires a non-empty expected_output.");
                }

                existingTestCase.Name = testCase.Name;
                existingTestCase.Inputs = testCase.Inputs;
                existingTestCase.ExpectedOutput = testCase.ExpectedOutput;

                await store.SaveTestCaseAsync(existingTestCase, ct);
                return Results.Ok(new { ok = true });
            }
        )
            .WithSummary("Update a test case");

        group.MapDelete(
            "/testcases/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var deleted = await store.DeleteTestCaseAsync(id, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
        )
            .WithSummary("Delete a test case");

        group.MapPost(
            "/testcases/{id}/execute",
            async (string id, IPromptEngineRepository store, PromptTester tester, CancellationToken ct) =>
            {
                var testCase = await store.GetTestCaseAsync(id, ct);
                if (testCase is null)
                    return Results.NotFound();

                var prompt = await store.GetPromptAsync(testCase.PromptId, ct);
                if (prompt is null)
                    return Results.NotFound();

                if (string.IsNullOrWhiteSpace(testCase.ExpectedOutput))
                {
                    return PromptEngineEndpointErrors.Validation("test_case_expected_output_required", "A structured test case requires a non-empty expected_output.");
                }

                var result = await tester.ExecuteAsync(prompt, testCase, ct);
                if (!result.IsSuccess)
                {
                    return PromptEngineEndpointErrors.Validation("test_case_execution_failed", result.Error ?? $"Execution of test case '{testCase.Name}' failed.");
                }

                await store.SaveExecutionAsync(result.Value!, ct);

                if (!string.IsNullOrWhiteSpace(prompt.CurrentVersionSnapshotId))
                {
                    var snapshot = await store.GetPromptVersionSnapshotAsync(prompt.CurrentVersionSnapshotId!, ct);
                    if (snapshot is not null)
                    {
                        snapshot.LastKnownScore = result.Value!.Score;
                        snapshot.LastKnownCost = result.Value.CostEstimate;
                        snapshot.LastKnownTokensUsed = result.Value.TokensUsed;
                        snapshot.LastExecutedAt = result.Value.ExecutedAt;
                        snapshot.ExecutionConfig = result.Value.ExecutionConfig;
                        await store.SavePromptVersionSnapshotAsync(snapshot, ct);
                    }
                }

                return Results.Ok(result.Value);
            }
        )
            .WithSummary("Run one test case against its prompt");

        group.MapPost(
            "/{promptId}/testcases/execute-all",
            async (string promptId, IPromptEngineRepository store, PromptTester tester, CancellationToken ct) =>
            {
                var prompt = await store.GetPromptAsync(promptId, ct);
                if (prompt is null)
                    return Results.NotFound();

                var testCases = await store.ListTestCasesAsync(promptId, ct);
                if (testCases.Count == 0)
                    return Results.BadRequest("No test cases found for this PromptEntity.");

                if (testCases.Any(testCase => string.IsNullOrWhiteSpace(testCase.ExpectedOutput)))
                {
                    return PromptEngineEndpointErrors.Validation("test_case_expected_output_required", "All persisted test cases must define a non-empty expected_output before running the test suite.");
                }

                var executions = new List<TestExecution>();

                foreach (var testCase in testCases)
                {
                    var result = await tester.ExecuteAsync(prompt, testCase, ct);
                    if (!result.IsSuccess)
                    {
                        return PromptEngineEndpointErrors.Validation("test_case_execution_failed", result.Error ?? $"Execution of test case '{testCase.Name}' failed.");
                    }

                    if (result.IsSuccess)
                    {
                        await store.SaveExecutionAsync(result.Value!, ct);

                        if (!string.IsNullOrWhiteSpace(prompt.CurrentVersionSnapshotId))
                        {
                            var snapshot = await store.GetPromptVersionSnapshotAsync(prompt.CurrentVersionSnapshotId!, ct);
                            if (snapshot is not null)
                            {
                                snapshot.LastKnownScore = result.Value!.Score;
                                snapshot.LastKnownCost = result.Value.CostEstimate;
                                snapshot.LastKnownTokensUsed = result.Value.TokensUsed;
                                snapshot.LastExecutedAt = result.Value.ExecutedAt;
                                snapshot.ExecutionConfig = result.Value.ExecutionConfig;
                                await store.SavePromptVersionSnapshotAsync(snapshot, ct);
                            }
                        }

                        executions.Add(result.Value!);
                    }
                }

                return Results.Ok(executions);
            }
        )
            .WithSummary("Run all the test cases of a prompt");

        group.MapGet(
            "/{promptId}/testcases/executions",
            async (string promptId, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var executions = await store.ListTestCaseExecutionsAsync(promptId, ct);
                return Results.Ok(executions);
            }
        )
            .WithSummary("List the test case executions of a prompt");

        group.MapGet(
            "/testcases/executions/{id}",
            async (string id, IPromptEngineRepository store, CancellationToken ct) =>
            {
                var execution = await store.GetExecutionAsync(id, ct);
                return execution is not null && string.Equals(execution.ExecutionKind, "test-case", StringComparison.OrdinalIgnoreCase) ? Results.Ok(execution) : Results.NotFound();
            }
        )
            .WithSummary("Get one test case execution");
    }
}
