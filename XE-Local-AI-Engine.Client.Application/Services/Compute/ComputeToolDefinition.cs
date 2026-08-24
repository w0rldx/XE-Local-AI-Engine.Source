namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Worker-side name / description / parameter-schema constants for the <c>run_python</c> compute tool. The handler
///     advertises its model-visible schema from here and the offer provider merges the same descriptor into the profile
///     pool, so what the model is offered can never drift from what the handler validates. The schema is advisory; the
///     handler's own validation is authoritative.
/// </summary>
internal static class ComputeToolDefinition
{
    /// <summary>The authoritative ceiling on a submitted script, enforced by the handler rather than by the schema.</summary>
    public const int CodeMaxLength = 20000;

    public const string ToolName = "run_python";

    public const string Description =
        "Run a short Python 3 script in a sandboxed, offline interpreter and return its exit code, stdout and stderr. "
        + "numpy, scipy and sympy are available; there is no network access, no access to the conversation, and nothing "
        + "written to disk survives the call. Print what you want to see — an expression's value is not returned on its "
        + "own. Use this to CHECK arithmetic, algebra, calculus and numeric claims before asserting them.";

    // Deliberately carries no `maxLength`: the authoritative ceiling is CodeMaxLength in the handler, and any bound past
    // 1024 is stripped from the llama.cpp wire anyway by LlamaGrammarToolSchemaCompatibility. Stating a bound here that
    // the grammar pass then removes would buy nothing and cost the offer a sanitizing rewrite on every request; a schema
    // with no repetition bound above the cap is grammar-safe by construction. See ComputeToolSchemaCompatibilityTests.
    /// <summary>The compute tool parameter schema: one required <c>code</c> string, nothing else.</summary>
    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "additionalProperties": false,
                                            "required": ["code"],
                                            "properties": {
                                              "code": { "type": "string", "minLength": 1 }
                                            }
                                          }
                                          """;
}
