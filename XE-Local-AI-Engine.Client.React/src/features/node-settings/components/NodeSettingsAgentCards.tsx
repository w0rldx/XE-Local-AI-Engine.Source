import { Select, Switch } from "@mantine/core";
import { IconArrowsMinimize, IconBulb, IconRobot, IconRuler, IconTool } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { nodeSettingsFieldLabel } from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import { type NodeSettingsTunableCardProps, TunableCard } from "@/features/node-settings/components/NodeSettingsTunableCards";
import {
	chatOutputCapModeSelectValues,
	defaultReasoningEffortSelectValues,
	shippedDefaults,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The chat and agent-run tunable cards, placed by NodeSettingsFieldsCard.

// Prepends the "use the shipped default" choice (value "", which the save sends as the clear) and names that default.
function withDefaultOption(
	t: TFunction,
	options: readonly { value: string; label: string }[],
	shippedValue: string,
): { value: string; label: string }[] {
	const shippedLabel = options.find((option) => option.value === shippedValue)?.label ?? shippedValue;
	return [
		{ value: "", label: t("pages.nodeSettings.fields.defaultOption", "Default ({{value}})", { value: shippedLabel }) },
		...options,
	];
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

export function NodeSettingsReasoningBudgetsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const tokens = t("pages.nodeSettings.fields.tokens", "tokens");
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.reasoningBudgets.title", "Thinking budgets")}
			icon={<IconBulb size={20} />}
			testId="node-settings-reasoning-budgets-card"
			onReset={() => {
				field.onChange("defaultReasoningEffort", "");
				field.onChange("reasoningBudgetMinimalTokens", "");
				field.onChange("reasoningBudgetLowTokens", "");
				field.onChange("reasoningBudgetMediumTokens", "");
				field.onChange("reasoningBudgetHighTokens", "");
			}}
		>
			<Select
				label={nodeSettingsFieldLabel(
					t,
					"defaultReasoningEffort",
					t("pages.nodeSettings.fields.defaultReasoningEffort.label", "Budget when no effort is chosen"),
				)}
				description={t(
					"pages.nodeSettings.fields.defaultReasoningEffort.description",
					"Which budget a local thinking model gets when a turn names no reasoning effort.",
				)}
				data={withDefaultOption(
					t,
					defaultReasoningEffortSelectValues.map((value) => ({
						value,
						label: t(`pages.nodeSettings.fields.defaultReasoningEffort.options.${value}`, value),
					})),
					shippedDefaults.defaultReasoningEffort,
				)}
				value={field.form.defaultReasoningEffort}
				onChange={(value) => field.onChange("defaultReasoningEffort", value ?? field.form.defaultReasoningEffort)}
				allowDeselect={false}
				data-testid="node-settings-default-reasoning-effort"
			/>
			<NodeSettingsNumberField
				{...field}
				field="reasoningBudgetMinimalTokens"
				label={t("pages.nodeSettings.fields.reasoningBudgetMinimalTokens.label", "Minimal effort")}
				bounds={bounds.tunables.reasoningBudgetMinimalTokens}
				unit={tokens}
				shippedDefault={shippedDefaults.reasoningBudgetMinimalTokens}
				testId="node-settings-reasoning-budget-minimal"
			/>
			<NodeSettingsNumberField
				{...field}
				field="reasoningBudgetLowTokens"
				label={t("pages.nodeSettings.fields.reasoningBudgetLowTokens.label", "Low effort")}
				bounds={bounds.tunables.reasoningBudgetLowTokens}
				unit={tokens}
				shippedDefault={shippedDefaults.reasoningBudgetLowTokens}
				testId="node-settings-reasoning-budget-low"
			/>
			<NodeSettingsNumberField
				{...field}
				field="reasoningBudgetMediumTokens"
				label={t("pages.nodeSettings.fields.reasoningBudgetMediumTokens.label", "Medium effort")}
				bounds={bounds.tunables.reasoningBudgetMediumTokens}
				unit={tokens}
				shippedDefault={shippedDefaults.reasoningBudgetMediumTokens}
				testId="node-settings-reasoning-budget-medium"
			/>
			<NodeSettingsNumberField
				{...field}
				field="reasoningBudgetHighTokens"
				label={t("pages.nodeSettings.fields.reasoningBudgetHighTokens.label", "High effort")}
				description={t(
					"pages.nodeSettings.fields.reasoningBudgetHighTokens.description",
					"How long a local model may think before it must answer. The model's window can lower it further.",
				)}
				bounds={bounds.tunables.reasoningBudgetHighTokens}
				unit={tokens}
				shippedDefault={shippedDefaults.reasoningBudgetHighTokens}
				testId="node-settings-reasoning-budget-high"
			/>
		</TunableCard>
	);
}

export function NodeSettingsOutputCapCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.chatOutputCap.title", "Answer length")}
			icon={<IconRuler size={20} />}
			testId="node-settings-output-cap-card"
			onReset={() => {
				field.onChange("chatOutputCapMode", "");
				field.onChange("chatOutputCapMaxTokens", "");
			}}
		>
			<Select
				label={nodeSettingsFieldLabel(
					t,
					"chatOutputCapMode",
					t("pages.nodeSettings.fields.chatOutputCapMode.label", "Answer length limit"),
				)}
				description={t(
					"pages.nodeSettings.fields.chatOutputCapMode.description",
					"Stops a local model that keeps repeating itself. A limit a request sets itself always wins.",
				)}
				data={withDefaultOption(
					t,
					chatOutputCapModeSelectValues.map((value) => ({
						value,
						label: t(`pages.nodeSettings.fields.chatOutputCapMode.options.${value}`, value),
					})),
					shippedDefaults.chatOutputCapMode,
				)}
				value={field.form.chatOutputCapMode}
				onChange={(value) => field.onChange("chatOutputCapMode", value ?? field.form.chatOutputCapMode)}
				allowDeselect={false}
				data-testid="node-settings-chat-output-cap-mode"
			/>
			<NodeSettingsNumberField
				{...field}
				field="chatOutputCapMaxTokens"
				label={t("pages.nodeSettings.fields.chatOutputCapMaxTokens.label", "Longest answer")}
				description={`${t(
					"pages.nodeSettings.fields.chatOutputCapMaxTokens.description",
					"The answer may use half the model's context window, but never more than this. A thinking model's thinking budget comes on top, so this is not the total.",
				)} ${t(
					"pages.nodeSettings.fields.chatOutputCapMaxTokens.timeoutHint",
					"On slow hardware a long answer can reach the message request timeout (Local chat runtime) before this limit. If answers end in a timeout, raise that timeout.",
				)}`}
				bounds={bounds.tunables.chatOutputCapMaxTokens}
				unit={t("pages.nodeSettings.fields.tokens", "tokens")}
				shippedDefault={shippedDefaults.chatOutputCapMaxTokens}
				testId="node-settings-chat-output-cap-max-tokens"
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
