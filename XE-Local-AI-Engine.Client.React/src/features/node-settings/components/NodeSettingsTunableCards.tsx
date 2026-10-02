import { Card, Group, Select, Stack, Switch, Title } from "@mantine/core";
import {
	IconArrowsMinimize,
	IconBox,
	IconCloudLock,
	IconDownload,
	IconHistory,
	IconLayoutKanban,
	IconMicrophone,
	IconRobot,
	IconSearch,
	IconSparkles,
	IconTool,
	IconWorldDownload,
} from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { nodeSettingsFieldLabel } from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import {
	containerRuntimeSelectValues,
	type NodeSettingsFieldBounds,
	type NodeSettingsFieldsForm,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The small cards for the curated runtime tunables, one per subsystem, placed by NodeSettingsFieldsCard.
export interface NodeSettingsTunableCardProps {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

function TunableCard({
	title,
	icon,
	testId,
	children,
}: {
	readonly title: string;
	readonly icon: ReactNode;
	readonly testId: string;
	readonly children: ReactNode;
}) {
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{title}
					</Title>
					{icon}
				</Group>
				{children}
			</Stack>
		</Card>
	);
}

export function NodeSettingsToolLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.toolLimits.title", "Tool and provider limits")}
			icon={<IconTool size={20} />}
			testId="node-settings-tool-limits-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="maxProviderCallsPerInvocation"
				label={t("pages.nodeSettings.fields.maxProviderCallsPerInvocation.label", "Model calls per agent run")}
				description={t(
					"pages.nodeSettings.fields.maxProviderCallsPerInvocation.description",
					"Stops an agent run that keeps calling the model, for example in a tool loop.",
				)}
				bounds={bounds.tunables.maxProviderCallsPerInvocation}
				testId="node-settings-max-provider-calls"
			/>
			<NodeSettingsNumberField
				{...field}
				field="customToolMaxTimeoutSeconds"
				label={t("pages.nodeSettings.fields.customToolMaxTimeoutSeconds.label", "Custom tool timeout ceiling")}
				description={t(
					"pages.nodeSettings.fields.customToolMaxTimeoutSeconds.description",
					"The longest timeout a custom tool may set for itself.",
				)}
				bounds={bounds.tunables.customToolMaxTimeoutSeconds}
				unit={t("pages.nodeSettings.fields.seconds", "seconds")}
				testId="node-settings-custom-tool-max-timeout"
			/>
		</TunableCard>
	);
}

export function NodeSettingsContainerRuntimeCard({ form, onChange }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.containerRuntime.title", "Container runtime")}
			icon={<IconBox size={20} />}
			testId="node-settings-container-runtime-card"
		>
			<Select
				label={nodeSettingsFieldLabel(
					t,
					"containerRuntimeSelection",
					t("pages.nodeSettings.fields.containerRuntimeSelection.label", "Runtime for external apps"),
				)}
				description={t(
					"pages.nodeSettings.fields.containerRuntimeSelection.description",
					"Which container runtime external apps use. An app can still override it.",
				)}
				data={containerRuntimeSelectValues.map((value) => ({
					value,
					label: t(`pages.nodeSettings.fields.containerRuntimeSelection.options.${value}`, value),
				}))}
				value={form.containerRuntimeSelection}
				onChange={(value) => onChange("containerRuntimeSelection", value ?? form.containerRuntimeSelection)}
				allowDeselect={false}
				data-testid="node-settings-container-runtime"
			/>
		</TunableCard>
	);
}

export function NodeSettingsDownloadLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.downloads.title", "Downloads")}
			icon={<IconDownload size={20} />}
			testId="node-settings-downloads-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="huggingFaceDownloadConnections"
				label={t("pages.nodeSettings.fields.huggingFaceDownloadConnections.label", "Parallel download connections")}
				bounds={bounds.tunables.huggingFaceDownloadConnections}
				testId="node-settings-hf-download-connections"
			/>
			<NodeSettingsNumberField
				{...field}
				field="huggingFaceDiskMarginBytes"
				label={t("pages.nodeSettings.fields.huggingFaceDiskMarginBytes.label", "Free disk space to keep")}
				description={t(
					"pages.nodeSettings.fields.huggingFaceDiskMarginBytes.description",
					"A download is refused if it would leave less free space than this.",
				)}
				bounds={bounds.huggingFaceDiskMarginBytes}
				unit="GB"
				wireUnit={t("pages.nodeSettings.fields.bytesShort", "B")}
				testId="node-settings-hf-disk-margin"
			/>
		</TunableCard>
	);
}

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
					"Results returned when the model does not ask for a number. At most the maximum below.",
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

export function NodeSettingsWebFetchLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.webFetch.title", "Page fetching")}
			icon={<IconWorldDownload size={20} />}
			testId="node-settings-web-fetch-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="webFetchTimeoutSeconds"
				label={t("pages.nodeSettings.fields.webFetchTimeoutSeconds.label", "Page fetch timeout")}
				bounds={bounds.tunables.webFetchTimeoutSeconds}
				unit={t("pages.nodeSettings.fields.seconds", "seconds")}
				testId="node-settings-web-fetch-timeout"
			/>
			<NodeSettingsNumberField
				{...field}
				field="webFetchMaxContentChars"
				label={t("pages.nodeSettings.fields.webFetchMaxContentChars.label", "Page text limit")}
				description={t(
					"pages.nodeSettings.fields.webFetchMaxContentChars.description",
					"The most characters of a fetched page the model receives.",
				)}
				bounds={bounds.tunables.webFetchMaxContentChars}
				unit={t("pages.nodeSettings.fields.characters", "characters")}
				testId="node-settings-web-fetch-max-chars"
			/>
		</TunableCard>
	);
}

export function NodeSettingsTranscriptionCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const minutes = t("pages.nodeSettings.fields.minutes", "minutes");
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.transcription.title", "Transcription")}
			icon={<IconMicrophone size={20} />}
			testId="node-settings-transcription-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="transcriptionIdleTimeoutMinutes"
				label={t("pages.nodeSettings.fields.transcriptionIdleTimeoutMinutes.label", "Unload an idle transcription model after")}
				bounds={bounds.tunables.transcriptionIdleTimeoutMinutes}
				unit={minutes}
				testId="node-settings-transcription-idle-timeout"
			/>
			<NodeSettingsNumberField
				{...field}
				field="transcriptionInferenceTimeoutMinutes"
				label={t("pages.nodeSettings.fields.transcriptionInferenceTimeoutMinutes.label", "Transcription timeout")}
				description={t(
					"pages.nodeSettings.fields.transcriptionInferenceTimeoutMinutes.description",
					"The longest one transcription job may run.",
				)}
				bounds={bounds.tunables.transcriptionInferenceTimeoutMinutes}
				unit={minutes}
				testId="node-settings-transcription-inference-timeout"
			/>
		</TunableCard>
	);
}

// Chat and agent-run knobs. The three tool-pipeline limits are restart-gated (badge from the shared label); the rest apply live.
export function NodeSettingsAgentRunLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const { form, onChange } = field;
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.agentRunLimits.title", "Agent limits")}
			icon={<IconRobot size={20} />}
			testId="node-settings-agent-run-limits-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="toolPipelineMaxIterationsPerRequest"
				label={t("pages.nodeSettings.fields.toolPipelineMaxIterationsPerRequest.label", "Tool rounds per request")}
				description={t(
					"pages.nodeSettings.fields.toolPipelineMaxIterationsPerRequest.description",
					"How many times the model may call tools before it must answer.",
				)}
				bounds={bounds.tunables.toolPipelineMaxIterationsPerRequest}
				testId="node-settings-tool-iterations"
			/>
			<NodeSettingsNumberField
				{...field}
				field="toolPipelineMaxToolResultChars"
				label={t("pages.nodeSettings.fields.toolPipelineMaxToolResultChars.label", "Tool result size")}
				description={t(
					"pages.nodeSettings.fields.toolPipelineMaxToolResultChars.description",
					"The most characters one tool result may hand the model.",
				)}
				bounds={bounds.tunables.toolPipelineMaxToolResultChars}
				unit={t("pages.nodeSettings.fields.characters", "characters")}
				testId="node-settings-tool-result-chars"
			/>
			<NodeSettingsNumberField
				{...field}
				field="toolPipelineMaxConsecutiveInvalidToolCalls"
				label={t("pages.nodeSettings.fields.toolPipelineMaxConsecutiveInvalidToolCalls.label", "Invalid tool calls in a row")}
				description={t(
					"pages.nodeSettings.fields.toolPipelineMaxConsecutiveInvalidToolCalls.description",
					"How many malformed calls to one tool in a row before the tool is refused.",
				)}
				bounds={bounds.tunables.toolPipelineMaxConsecutiveInvalidToolCalls}
				testId="node-settings-invalid-tool-calls"
			/>
			<NodeSettingsNumberField
				{...field}
				field="providerBudgetMaxCumulativeInputTokens"
				label={t("pages.nodeSettings.fields.providerBudgetMaxCumulativeInputTokens.label", "Input tokens per agent run")}
				description={t(
					"pages.nodeSettings.fields.providerBudgetMaxCumulativeInputTokens.description",
					"Stops an agent run once it has sent this many input tokens in total.",
				)}
				bounds={bounds.tunables.providerBudgetMaxCumulativeInputTokens}
				unit={t("pages.nodeSettings.fields.tokens", "tokens")}
				testId="node-settings-cumulative-input-tokens"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.providerRetryEnabled.label", "Retry failed model requests")}
				description={t(
					"pages.nodeSettings.fields.providerRetryEnabled.description",
					"Retry a model request that fails before its first token arrives.",
				)}
				checked={form.providerRetryEnabled}
				onChange={(event) => onChange("providerRetryEnabled", event.currentTarget.checked)}
				data-testid="node-settings-provider-retry-enabled"
			/>
			<NodeSettingsNumberField
				{...field}
				field="providerMaxRetries"
				label={t("pages.nodeSettings.fields.providerMaxRetries.label", "Retries per model request")}
				description={t(
					"pages.nodeSettings.fields.providerMaxRetries.description",
					"How often a failed model request is retried.",
				)}
				bounds={bounds.tunables.providerMaxRetries}
				testId="node-settings-provider-max-retries"
			/>
			<NodeSettingsNumberField
				{...field}
				field="spawnMaxConcurrent"
				label={t("pages.nodeSettings.fields.spawnMaxConcurrent.label", "Sub-agents at once")}
				description={t(
					"pages.nodeSettings.fields.spawnMaxConcurrent.description",
					"How many sub-agents one run may have working at the same time.",
				)}
				bounds={bounds.tunables.spawnMaxConcurrent}
				testId="node-settings-spawn-max-concurrent"
			/>
			<NodeSettingsNumberField
				{...field}
				field="spawnMaxCloud"
				label={t("pages.nodeSettings.fields.spawnMaxCloud.label", "Cloud sub-agents per run")}
				description={t(
					"pages.nodeSettings.fields.spawnMaxCloud.description",
					"How many sub-agents on a cloud model one run may start. 0 allows none.",
				)}
				bounds={bounds.tunables.spawnMaxCloud}
				testId="node-settings-spawn-max-cloud"
			/>
			<NodeSettingsNumberField
				{...field}
				field="spawnQueueWaitSeconds"
				label={t("pages.nodeSettings.fields.spawnQueueWaitSeconds.label", "Sub-agent queue wait")}
				description={t(
					"pages.nodeSettings.fields.spawnQueueWaitSeconds.description",
					"How long a sub-agent waits for its busy model. 0 rejects it at once.",
				)}
				bounds={bounds.tunables.spawnQueueWaitSeconds}
				unit={t("pages.nodeSettings.fields.seconds", "seconds")}
				testId="node-settings-spawn-queue-wait"
			/>
		</TunableCard>
	);
}

// Chat and agent-run knobs. The three tool-pipeline limits are restart-gated (badge from the shared label); the rest apply live.
export function NodeSettingsContextCompactionCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const { form, onChange } = field;
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.contextCompaction.title", "Context & compaction")}
			icon={<IconArrowsMinimize size={20} />}
			testId="node-settings-context-compaction-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="defaultContextTokens"
				label={t("pages.nodeSettings.fields.defaultContextTokens.label", "Default context window")}
				description={t(
					"pages.nodeSettings.fields.defaultContextTokens.description",
					"The context size assumed when neither the chat nor the model sets one.",
				)}
				bounds={bounds.tunables.defaultContextTokens}
				unit={t("pages.nodeSettings.fields.tokens", "tokens")}
				testId="node-settings-default-context-tokens"
			/>
			<NodeSettingsNumberField
				{...field}
				field="providerBudgetRecentMessagesToKeep"
				label={t("pages.nodeSettings.fields.providerBudgetRecentMessagesToKeep.label", "Messages kept per model call")}
				description={t(
					"pages.nodeSettings.fields.providerBudgetRecentMessagesToKeep.description",
					"The most recent messages a model call always keeps when history is trimmed.",
				)}
				bounds={bounds.tunables.providerBudgetRecentMessagesToKeep}
				testId="node-settings-provider-recent-messages"
			/>
			<NodeSettingsNumberField
				{...field}
				field="contextBudgetRecentTurnKeepCount"
				label={t("pages.nodeSettings.fields.contextBudgetRecentTurnKeepCount.label", "Turns kept when trimming")}
				description={t(
					"pages.nodeSettings.fields.contextBudgetRecentTurnKeepCount.description",
					"The most recent turns that are never trimmed from a long chat.",
				)}
				bounds={bounds.tunables.contextBudgetRecentTurnKeepCount}
				testId="node-settings-recent-turn-keep-count"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.compactionAutoEnabled.label", "Compact long chats automatically")}
				description={t(
					"pages.nodeSettings.fields.compactionAutoEnabled.description",
					"Summarise the older part of a chat once it fills most of the context window.",
				)}
				checked={form.compactionAutoEnabled}
				onChange={(event) => onChange("compactionAutoEnabled", event.currentTarget.checked)}
				data-testid="node-settings-compaction-auto-enabled"
			/>
			<NodeSettingsNumberField
				{...field}
				field="compactionAutoCompactPercent"
				label={t("pages.nodeSettings.fields.compactionAutoCompactPercent.label", "Compaction threshold")}
				description={t(
					"pages.nodeSettings.fields.compactionAutoCompactPercent.description",
					"Compact once the history fills this share of the usable context window.",
				)}
				bounds={bounds.tunables.compactionAutoCompactPercent}
				unit={t("pages.nodeSettings.fields.percent", "%")}
				testId="node-settings-compaction-percent"
			/>
			<NodeSettingsNumberField
				{...field}
				field="compactionRecentMessagesVerbatim"
				label={t("pages.nodeSettings.fields.compactionRecentMessagesVerbatim.label", "Messages kept word for word")}
				description={t(
					"pages.nodeSettings.fields.compactionRecentMessagesVerbatim.description",
					"The most recent messages a compaction leaves unchanged.",
				)}
				bounds={bounds.tunables.compactionRecentMessagesVerbatim}
				testId="node-settings-compaction-recent-messages"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.compactionDistillEnabled.label", "Keep a running chat summary")}
				description={t(
					"pages.nodeSettings.fields.compactionDistillEnabled.description",
					"Distil facts and decisions from a chat in the background.",
				)}
				checked={form.compactionDistillEnabled}
				onChange={(event) => onChange("compactionDistillEnabled", event.currentTarget.checked)}
				data-testid="node-settings-compaction-distill-enabled"
			/>
			<NodeSettingsNumberField
				{...field}
				field="maxInlinedAttachmentChars"
				label={t("pages.nodeSettings.fields.maxInlinedAttachmentChars.label", "Attachment text per turn")}
				description={t(
					"pages.nodeSettings.fields.maxInlinedAttachmentChars.description",
					"The most characters of attached documents included in one chat turn.",
				)}
				bounds={bounds.tunables.maxInlinedAttachmentChars}
				unit={t("pages.nodeSettings.fields.characters", "characters")}
				testId="node-settings-max-inlined-attachment-chars"
			/>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeChatTopK"
				label={t("pages.nodeSettings.fields.knowledgeChatTopK.label", "Knowledge passages per turn")}
				description={t(
					"pages.nodeSettings.fields.knowledgeChatTopK.description",
					"How many knowledge-base passages ground one chat turn.",
				)}
				bounds={bounds.tunables.knowledgeChatTopK}
				testId="node-settings-knowledge-chat-top-k"
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

// Its own card, deliberately apart from the external-access presets: a preset never changes this switch.
export function NodeSettingsCloudAccessCard({ form, onChange }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.cloudModelAccess.title", "Cloud models and local data")}
			icon={<IconCloudLock size={20} />}
			testId="node-settings-cloud-access-card"
		>
			<Switch
				label={t("pages.nodeSettings.fields.allowCloudModelAccess.label", "Let cloud models read local data")}
				description={t(
					"pages.nodeSettings.fields.allowCloudModelAccess.description",
					"Off by default. When on, cloud-hosted models also get the knowledge-base and workspace tools, attachments and playbook memory, which then leave this machine.",
				)}
				checked={form.allowCloudModelAccess}
				onChange={(event) => onChange("allowCloudModelAccess", event.currentTarget.checked)}
				data-testid="node-settings-allow-cloud-model-access"
			/>
		</TunableCard>
	);
}

export function NodeSettingsRetentionCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const { form, onChange } = field;
	const days = t("pages.nodeSettings.fields.days", "days");
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.retention.title", "Retention and storage")}
			icon={<IconHistory size={20} />}
			testId="node-settings-retention-card"
		>
			<Switch
				label={t("pages.nodeSettings.fields.chatRetentionEnabled.label", "Delete old conversations")}
				description={t(
					"pages.nodeSettings.fields.chatRetentionEnabled.description",
					"Permanently delete conversations, with their uploads and messages, once they are inactive for the window below.",
				)}
				checked={form.chatRetentionEnabled}
				onChange={(event) => onChange("chatRetentionEnabled", event.currentTarget.checked)}
				data-testid="node-settings-chat-retention-enabled"
			/>
			<NodeSettingsNumberField
				{...field}
				field="chatRetentionDays"
				label={t("pages.nodeSettings.fields.chatRetentionDays.label", "Conversation retention")}
				bounds={bounds.tunables.chatRetentionDays}
				unit={days}
				testId="node-settings-chat-retention-days"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.agentExecutionLogRetentionEnabled.label", "Delete old agent run logs")}
				description={t(
					"pages.nodeSettings.fields.agentExecutionLogRetentionEnabled.description",
					"Delete agent run records, which feed the usage page, once they are older than the window below.",
				)}
				checked={form.agentExecutionLogRetentionEnabled}
				onChange={(event) => onChange("agentExecutionLogRetentionEnabled", event.currentTarget.checked)}
				data-testid="node-settings-agent-log-retention-enabled"
			/>
			<NodeSettingsNumberField
				{...field}
				field="agentExecutionLogRetentionDays"
				label={t("pages.nodeSettings.fields.agentExecutionLogRetentionDays.label", "Agent run log retention")}
				bounds={bounds.tunables.agentExecutionLogRetentionDays}
				unit={days}
				testId="node-settings-agent-log-retention-days"
			/>
			<NodeSettingsNumberField
				{...field}
				field="schedulerHistoryRetentionDays"
				label={t("pages.nodeSettings.fields.schedulerHistoryRetentionDays.label", "Scheduled job history")}
				bounds={bounds.tunables.schedulerHistoryRetentionDays}
				unit={days}
				testId="node-settings-scheduler-history-days"
			/>
			<NodeSettingsNumberField
				{...field}
				field="nodeDbBackupRetainCount"
				label={t("pages.nodeSettings.fields.nodeDbBackupRetainCount.label", "Database backups kept")}
				description={t(
					"pages.nodeSettings.fields.nodeDbBackupRetainCount.description",
					"A backup is taken before each database update; older ones beyond this count are removed.",
				)}
				bounds={bounds.tunables.nodeDbBackupRetainCount}
				testId="node-settings-db-backup-retain-count"
			/>
			<NodeSettingsNumberField
				{...field}
				field="benchmarkKldCacheMaxBytes"
				label={t("pages.nodeSettings.fields.benchmarkKldCacheMaxBytes.label", "Benchmark cache size")}
				description={t(
					"pages.nodeSettings.fields.benchmarkKldCacheMaxBytes.description",
					"Disk space for the base-model files quality benchmarks compare against; the least recently used are removed first.",
				)}
				bounds={bounds.tunables.benchmarkKldCacheMaxBytes}
				unit="GB"
				wireUnit={t("pages.nodeSettings.fields.bytesShort", "B")}
				testId="node-settings-benchmark-kld-cache"
			/>
		</TunableCard>
	);
}

// The graph-workflow, work-session and development limits. All seven apply after a node restart.
export function NodeSettingsWorkspaceLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const seconds = t("pages.nodeSettings.fields.seconds", "seconds");
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.workspaceLimits.title", "Workflows, sessions and development")}
			icon={<IconLayoutKanban size={20} />}
			testId="node-settings-workspace-limits-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="graphWorkflowMaxConcurrentRuns"
				label={t("pages.nodeSettings.fields.graphWorkflowMaxConcurrentRuns.label", "Graph workflow runs at once")}
				description={t(
					"pages.nodeSettings.fields.graphWorkflowMaxConcurrentRuns.description",
					"Runs above this wait for a slot; they are not refused.",
				)}
				bounds={bounds.tunables.graphWorkflowMaxConcurrentRuns}
				testId="node-settings-graph-workflow-max-runs"
			/>
			<NodeSettingsNumberField
				{...field}
				field="graphWorkflowDefaultNodeTimeoutSeconds"
				label={t("pages.nodeSettings.fields.graphWorkflowDefaultNodeTimeoutSeconds.label", "Graph workflow step timeout")}
				description={t(
					"pages.nodeSettings.fields.graphWorkflowDefaultNodeTimeoutSeconds.description",
					"How long a workflow step may run when the step sets no timeout of its own.",
				)}
				bounds={bounds.tunables.graphWorkflowDefaultNodeTimeoutSeconds}
				unit={seconds}
				testId="node-settings-graph-workflow-node-timeout"
			/>
			<NodeSettingsNumberField
				{...field}
				field="workSessionMaxStepsPerRun"
				label={t("pages.nodeSettings.fields.workSessionMaxStepsPerRun.label", "Work session steps per run")}
				description={t(
					"pages.nodeSettings.fields.workSessionMaxStepsPerRun.description",
					"Steps one start or resume may take before the session pauses with a checkpoint.",
				)}
				bounds={bounds.tunables.workSessionMaxStepsPerRun}
				testId="node-settings-work-session-max-steps"
			/>
			<NodeSettingsNumberField
				{...field}
				field="workSessionMaxConcurrentSessions"
				label={t("pages.nodeSettings.fields.workSessionMaxConcurrentSessions.label", "Work sessions at once")}
				bounds={bounds.tunables.workSessionMaxConcurrentSessions}
				testId="node-settings-work-session-max-concurrent"
			/>
			<NodeSettingsNumberField
				{...field}
				field="developmentMaxAttemptDurationSeconds"
				label={t("pages.nodeSettings.fields.developmentMaxAttemptDurationSeconds.label", "Development attempt time limit")}
				bounds={bounds.tunables.developmentMaxAttemptDurationSeconds}
				unit={seconds}
				testId="node-settings-development-max-duration"
			/>
			<NodeSettingsNumberField
				{...field}
				field="developmentMaxToolCalls"
				label={t("pages.nodeSettings.fields.developmentMaxToolCalls.label", "Development tool calls per attempt")}
				bounds={bounds.tunables.developmentMaxToolCalls}
				testId="node-settings-development-max-tool-calls"
			/>
			<NodeSettingsNumberField
				{...field}
				field="developmentMaxOutputTokens"
				label={t("pages.nodeSettings.fields.developmentMaxOutputTokens.label", "Development output tokens per attempt")}
				bounds={bounds.tunables.developmentMaxOutputTokens}
				testId="node-settings-development-max-output-tokens"
			/>
		</TunableCard>
	);
}
