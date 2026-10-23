namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class Agents
    {
        public const string Definitions = "agents";
        public const string DefinitionById = "agents/{agentDefinitionId}";

        // Distinct literal segment under the agents surface so it cannot collide with DefinitionById.
        public const string ToolCapableModels = "agents/tool-capable-models";

        // The seeded Default Assistant's server-computed tool offer for a model; a literal segment like the one above.
        public const string DefaultAssistantToolOffer = "agents/default-tool-offer";

        // AI-assisted drafting. A literal segment under the collection, like the template actions below, so it can
        // never be parsed as an {agentDefinitionId}. Writes nothing — the draft only populates the operator's form.
        public const string Draft = "agents/draft";

        // Curated starter-pack catalog (GET list) and the operator-triggered import action. Literal segments after
        // "agents" keep these distinct from the {agentDefinitionId} route param.
        public const string Templates = "agents/templates";
        public const string TemplateImport = "agents/templates/import";

        // Per-agent playbook actions nested under the agent definition.
        public const string Playbook = "agents/{agentDefinitionId}/playbook";
        public const string PlaybookActionById = "agents/{agentDefinitionId}/playbook/{actionId}";

        // Analysis and review actions use literal segments so they remain distinct from action-id routes.
        public const string PlaybookAnalyze = "agents/{agentDefinitionId}/playbook/analyze";
        public const string PlaybookActionPromote = "agents/{agentDefinitionId}/playbook/{actionId}/promote";
        public const string PlaybookActionReject = "agents/{agentDefinitionId}/playbook/{actionId}/reject";
        public const string PlaybookActionSuggested = "agents/{agentDefinitionId}/playbook/{actionId}/suggested";

        // Golden-conversation evaluation for a specific suggested playbook action.
        public const string PlaybookActionEval = "agents/{agentDefinitionId}/playbook/{actionId}/eval";

        // Per-agent golden conversation set for manual authoring.
        public const string GoldenConversations = "agents/{agentDefinitionId}/golden-conversations";
        public const string GoldenConversation = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}";

        // On-demand thumbs-up harvest and per-candidate approval. Literal action segments keep collection actions
        // distinct from golden-conversation id routes.
        public const string GoldenConversationsHarvest = "agents/{agentDefinitionId}/golden-conversations/harvest";
        public const string GoldenConversationApprove = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}/approve";

        // Read-only per-agent feedback insights over message feedback aggregates.
        public const string FeedbackInsights = "agents/{agentDefinitionId}/feedback-insights";

        // Read-only cohort monitoring for enabled playbook actions.
        public const string PlaybookMonitor = "agents/{agentDefinitionId}/playbook/monitor";

        // Read-only adaptive-memory execution-log diagnostics (metadata only — no message content). Literal
        // "execution-logs" segment keeps it distinct from the {agentDefinitionId} route param.
        public const string ExecutionLogs = "agents/{agentDefinitionId}/execution-logs";

        // Read-only, versioned durable run-envelope lifecycle records (metadata only — no message content), optionally
        // filtered by conversationId. Literal "run-envelopes" segment keeps it distinct from the {agentDefinitionId} param.
        public const string RunEnvelopes = "agents/run-envelopes";

        // Read-only token-usage aggregation over the run-envelope ledger, grouped by model and UTC day (metadata only —
        // token counts, no content). Literal "usage-summary" segment keeps it distinct from the {agentDefinitionId} param.
        public const string UsageSummary = "agents/usage-summary";
    }
}
