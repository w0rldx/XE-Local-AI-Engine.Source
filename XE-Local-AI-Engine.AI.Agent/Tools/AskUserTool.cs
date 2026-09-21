namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     The shared, single-source definition of the <c>ask_user</c> tool: its name, model-visible description and
///     parameter schema.
/// </summary>
/// <remarks>
///     Lives in the agent layer because the ClientLocal handler, the offer providers and the resolvers all need the
///     identical strings. GRAMMAR: llama.cpp compiles every offered schema into one combined GBNF grammar whose
///     repetition ceiling is spent ACROSS the whole <c>tools</c> array (docs/agent-knowledge.md §3), so this schema
///     carries no <c>maxLength</c> and no <c>pattern</c>, keeps item bounds tiny and leaves the "Other" choice to the
///     CLIENT. Never add string bounds without re-running <c>scripts/run-tool-grammar-smoke-local.sh</c>.
/// </remarks>
public static class AskUserTool
{
    /// <summary>The tool name, matched by name at every offer and resolution seam.</summary>
    public const string ToolName = "ask_user";

    /// <summary>Model-visible description, deliberately prescriptive about WHEN to call the tool.</summary>
    /// <remarks>
    ///     An unguided model either never asks and guesses, or asks constantly. The "do not ask what you can determine
    ///     yourself" clause is the one that keeps it from degenerating into a confirmation prompt on every step.
    /// </remarks>
    public const string Description =
        "Asks the user a multiple-choice question and waits for their answer before continuing. Use this when a "
        + "decision is genuinely the user's to make and different answers would lead to materially different work — "
        + "an ambiguous requirement, a choice between approaches with real trade-offs, or a preference you cannot "
        + "infer. Do NOT use it for anything you can determine yourself from the conversation or the available tools, "
        + "and do NOT use it to confirm work you were already asked to do. Mark the option you would pick with "
        + "\"recommended\": true when you have a genuine recommendation.";

    /// <summary>
    ///     Model-visible JSON schema. Kept flat and bound-free on purpose — see the grammar note on the class.
    /// </summary>
    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "properties": {
                                              "questions": {
                                                "type": "array",
                                                "minItems": 1,
                                                "maxItems": 4,
                                                "description": "The questions to ask. Ask several at once only when they are genuinely independent.",
                                                "items": {
                                                  "type": "object",
                                                  "properties": {
                                                    "header": {
                                                      "type": "string",
                                                      "description": "A very short label for the question, about 12 characters, e.g. 'Auth method'."
                                                    },
                                                    "question": {
                                                      "type": "string",
                                                      "description": "The question to ask, phrased as a complete sentence ending in a question mark."
                                                    },
                                                    "multiSelect": {
                                                      "type": "boolean",
                                                      "description": "True when the user may choose more than one option. Defaults to false (choose exactly one)."
                                                    },
                                                    "options": {
                                                      "type": "array",
                                                      "minItems": 2,
                                                      "maxItems": 6,
                                                      "description": "The choices offered. A free-text 'Other' choice is always added for the user automatically, so never add one yourself.",
                                                      "items": {
                                                        "type": "object",
                                                        "properties": {
                                                          "label": {
                                                            "type": "string",
                                                            "description": "The choice as the user sees it. Keep it to a few words."
                                                          },
                                                          "description": {
                                                            "type": "string",
                                                            "description": "One sentence on what this choice means or what follows from it."
                                                          },
                                                          "recommended": {
                                                            "type": "boolean",
                                                            "description": "True to mark this as your recommended choice. Mark at most one option per question."
                                                          }
                                                        },
                                                        "required": ["label"]
                                                      }
                                                    }
                                                  },
                                                  "required": ["question", "options"]
                                                }
                                              }
                                            },
                                            "required": ["questions"]
                                          }
                                          """;
}
