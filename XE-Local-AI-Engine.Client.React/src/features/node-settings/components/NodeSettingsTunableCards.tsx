import { Button, Card, Group, Select, Stack, Switch, Title } from "@mantine/core";
import {
	IconBox,
	IconCloudLock,
	IconDownload,
	IconHistory,
	IconLayoutKanban,
	IconMicrophone,
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
	type NodeSettingsModelOption,
	shippedDefaults,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The card shell and props every curated runtime-tunable card shares, plus the cards for the subsystems without a
// card file of their own (agent and knowledge cards live beside this one). NodeSettingsFieldsCard places them all.
export interface NodeSettingsTunableCardProps {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

export function TunableCard({
	title,
	icon,
	testId,
	onReset,
	children,
}: {
	readonly title: string;
	readonly icon: ReactNode;
	readonly testId: string;
	// Optional card-level reset: blanks every field of the card back to the shipped default (the save sends the clear).
	readonly onReset?: () => void;
	readonly children: ReactNode;
}) {
	const { t } = useTranslation();
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{title}
					</Title>
					<Group gap="xs" wrap="nowrap">
						{onReset !== undefined && (
							<Button variant="subtle" size="compact-sm" onClick={onReset} data-testid={`${testId}-reset`}>
								{t("pages.nodeSettings.fields.resetCard", "Reset to defaults")}
							</Button>
						)}
						{icon}
					</Group>
				</Group>
				{children}
			</Stack>
		</Card>
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

// Its own card, deliberately apart from the external-access presets: a preset never changes these switches.
export function NodeSettingsCloudAccessCard({ form, onChange }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.cloudModelAccess.title", "Cloud models")}
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
			<Switch
				label={t("pages.nodeSettings.fields.allowCloudModelUnattendedRuns.label", "Let cloud models run unattended")}
				description={t(
					"pages.nodeSettings.fields.allowCloudModelUnattendedRuns.description",
					"Off by default. When on, scheduled agent runs, graph workflow model nodes and integration triggers may run on a cloud-hosted model. Their prompts, upstream content and trigger payloads then leave this machine.",
				)}
				checked={form.allowCloudModelUnattendedRuns}
				onChange={(event) => onChange("allowCloudModelUnattendedRuns", event.currentTarget.checked)}
				data-testid="node-settings-allow-cloud-model-unattended-runs"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.allowCloudModelWebTools.label", "Let cloud models use web tools")}
				description={t(
					"pages.nodeSettings.fields.allowCloudModelWebTools.description",
					"Off by default. When on, cloud-hosted models get web_search, web_fetch and HTTP custom tools while Web access is enabled. Requests follow the usual web consent and tool approval rules.",
				)}
				checked={form.allowCloudModelWebTools}
				onChange={(event) => onChange("allowCloudModelWebTools", event.currentTarget.checked)}
				data-testid="node-settings-allow-cloud-model-web-tools"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.allowCloudModelMcpTools.label", "Let cloud models use MCP tools")}
				description={t(
					"pages.nodeSettings.fields.allowCloudModelMcpTools.description",
					"Off by default. When on, cloud-hosted models get the tools of connected MCP servers, whose results then leave this machine.",
				)}
				checked={form.allowCloudModelMcpTools}
				onChange={(event) => onChange("allowCloudModelMcpTools", event.currentTarget.checked)}
				data-testid="node-settings-allow-cloud-model-mcp-tools"
			/>
			<Switch
				label={t("pages.nodeSettings.fields.allowCloudModelSubAgents.label", "Let cloud models delegate to sub-agents")}
				description={t(
					"pages.nodeSettings.fields.allowCloudModelSubAgents.description",
					"Off by default. When on, a cloud-hosted model may start sub-agents. Each sub-agent keeps its own model's permissions, and the cloud sub-agent cap still applies.",
				)}
				checked={form.allowCloudModelSubAgents}
				onChange={(event) => onChange("allowCloudModelSubAgents", event.currentTarget.checked)}
				data-testid="node-settings-allow-cloud-model-sub-agents"
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

// The graph-workflow, work-session and development limits. Those seven apply after a node restart; the two intent
// settings beside them are read per request.
export function NodeSettingsWorkspaceLimitsCard({
	bounds,
	intentRouterOptions,
	...field
}: NodeSettingsTunableCardProps & { readonly intentRouterOptions: readonly NodeSettingsModelOption[] }) {
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
			<Select
				label={nodeSettingsFieldLabel(
					t,
					"intentRouterDefinitionId",
					t("pages.nodeSettings.fields.intentRouterDefinitionId.label", "Intent router workflow"),
				)}
				description={t(
					"pages.nodeSettings.fields.intentRouterDefinitionId.description",
					"The graph workflow that picks a workflow for a spoken or typed command when no alias matches.",
				)}
				data={[
					{ value: "", label: t("pages.nodeSettings.fields.intentRouterDefinitionId.none", "None (aliases only)") },
					...intentRouterOptions,
				]}
				value={field.form.intentRouterDefinitionId}
				onChange={(value) => field.onChange("intentRouterDefinitionId", value ?? "")}
				allowDeselect={false}
				data-testid="node-settings-intent-router"
			/>
			<NodeSettingsNumberField
				{...field}
				field="intentMaxUtteranceAgeSeconds"
				label={t("pages.nodeSettings.fields.intentMaxUtteranceAgeSeconds.label", "Maximum utterance age")}
				description={t(
					"pages.nodeSettings.fields.intentMaxUtteranceAgeSeconds.description",
					"An older command is refused as a replay and has to be said again.",
				)}
				bounds={bounds.tunables.intentMaxUtteranceAgeSeconds}
				unit={seconds}
				shippedDefault={shippedDefaults.intentMaxUtteranceAgeSeconds}
				testId="node-settings-intent-max-utterance-age"
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
