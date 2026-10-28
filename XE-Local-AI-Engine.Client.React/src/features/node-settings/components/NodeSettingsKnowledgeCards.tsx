import { Switch } from "@mantine/core";
import { IconSearch, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { nodeSettingsFieldLabel } from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import { type NodeSettingsTunableCardProps, TunableCard } from "@/features/node-settings/components/NodeSettingsTunableCards";

// The knowledge-base tunable cards, placed by NodeSettingsFieldsCard.
export function NodeSettingsKnowledgeSearchCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.knowledgeSearch.title", "Knowledge search")}
			icon={<IconSearch size={20} />}
			testId="node-settings-knowledge-search-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeSearchDefaultResults"
				label={t("pages.nodeSettings.fields.knowledgeSearchDefaultResults.label", "Default results")}
				description={t(
					"pages.nodeSettings.fields.knowledgeSearchDefaultResults.description",
					"Results for the model's knowledge searches when it does not ask for a number. At most the maximum below.",
				)}
				bounds={bounds.tunables.knowledgeSearchDefaultResults}
				testId="node-settings-knowledge-default-results"
			/>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeSearchMaxResults"
				label={t("pages.nodeSettings.fields.knowledgeSearchMaxResults.label", "Maximum results")}
				bounds={bounds.tunables.knowledgeSearchMaxResults}
				testId="node-settings-knowledge-max-results"
			/>
		</TunableCard>
	);
}

export function NodeSettingsKnowledgeRetrievalCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const { form, onChange } = field;
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.knowledgeRetrieval.title", "Retrieval and knowledge tools")}
			icon={<IconSparkles size={20} />}
			testId="node-settings-knowledge-retrieval-card"
		>
			<Switch
				label={t("pages.nodeSettings.fields.knowledgeAgentToolsEnabled.label", "Knowledge tools for agents")}
				description={t(
					"pages.nodeSettings.fields.knowledgeAgentToolsEnabled.description",
					"Let agents search and read the knowledge base. Off withholds the tools from every model.",
				)}
				checked={form.knowledgeAgentToolsEnabled}
				onChange={(event) => onChange("knowledgeAgentToolsEnabled", event.currentTarget.checked)}
				data-testid="node-settings-knowledge-agent-tools-enabled"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.knowledgeAdaptiveRerankingEnabled.label", "Rerank only when results are unclear")}
				description={t(
					"pages.nodeSettings.fields.knowledgeAdaptiveRerankingEnabled.description",
					"Skip the reranker when keyword and meaning search agree, or when the latency budget is nearly spent.",
				)}
				checked={form.knowledgeAdaptiveRerankingEnabled}
				onChange={(event) => onChange("knowledgeAdaptiveRerankingEnabled", event.currentTarget.checked)}
				data-testid="node-settings-knowledge-adaptive-reranking"
			/>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeRetrievalLatencyBudgetMs"
				label={t("pages.nodeSettings.fields.knowledgeRetrievalLatencyBudgetMs.label", "Retrieval latency budget")}
				description={t(
					"pages.nodeSettings.fields.knowledgeRetrievalLatencyBudgetMs.description",
					"A rerank still running after this long is dropped and the search returns its first order.",
				)}
				bounds={bounds.tunables.knowledgeRetrievalLatencyBudgetMs}
				unit={t("pages.nodeSettings.fields.milliseconds", "milliseconds")}
				testId="node-settings-knowledge-latency-budget"
			/>
			<Switch
				label={nodeSettingsFieldLabel(
					t,
					"knowledgeScheduledReindexEnabled",
					t("pages.nodeSettings.fields.knowledgeScheduledReindexEnabled.label", "Reindex after an embedding model change"),
				)}
				description={t(
					"pages.nodeSettings.fields.knowledgeScheduledReindexEnabled.description",
					"Periodically re-queue documents whose vectors came from an older embedding model.",
				)}
				checked={form.knowledgeScheduledReindexEnabled}
				onChange={(event) => onChange("knowledgeScheduledReindexEnabled", event.currentTarget.checked)}
				data-testid="node-settings-knowledge-scheduled-reindex"
			/>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeScheduledReindexIntervalMinutes"
				label={t("pages.nodeSettings.fields.knowledgeScheduledReindexIntervalMinutes.label", "Reindex check interval")}
				bounds={bounds.tunables.knowledgeScheduledReindexIntervalMinutes}
				unit={t("pages.nodeSettings.fields.minutes", "minutes")}
				testId="node-settings-knowledge-reindex-interval"
			/>
		</TunableCard>
	);
}
