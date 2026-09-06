// The Agent node's body. Split out of `GraphWorkflowNodeConfigPanel` for size only: it owns no state, and the panel
// still holds the touched-field bookkeeping that decides when a Zod message is shown.

import { Group, Select, Switch, Textarea } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { GraphWorkflowJsonField } from "@/features/graphWorkflows/components/config/GraphWorkflowJsonField";
import { withCurrentValue } from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";

type AgentNodeData = Extract<GraphWorkflowCanvasNodeData, { kind: "Agent" }>;

/**
 * `GraphWorkflowGraph.cs`'s `ReasoningEfforts`, verbatim. The graph parser refuses anything outside the set at save
 * time, so this is a picker rather than a text field — and it deliberately does NOT carry the agent surface's `auto`,
 * which that parser does not accept.
 */
const reasoningEfforts = ["none", "low", "medium", "high"] as const;

export interface GraphWorkflowAgentConfigFormProps {
	readonly node: AgentNodeData;
	readonly onChange: (patch: Partial<AgentNodeData>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	readonly agentOptions: readonly { readonly value: string; readonly label: string }[];
	readonly modelOptions: readonly { readonly value: string; readonly label: string }[];
	readonly readOnly?: boolean;
}

export function GraphWorkflowAgentConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	agentOptions,
	modelOptions,
	readOnly = false,
}: GraphWorkflowAgentConfigFormProps) {
	const { t } = useTranslation();

	return (
		<>
			<Select
				label={t("pages.graphWorkflows.config.agent", "Agent")}
				placeholder={t("pages.graphWorkflows.config.agentPlaceholder", "This node's default agent")}
				data={withCurrentValue(agentOptions, node.agentDefinitionId)}
				value={node.agentDefinitionId}
				clearable={true}
				searchable={true}
				disabled={readOnly}
				onChange={(value) => onChange({ agentDefinitionId: value })}
				data-testid="gw-node-config-agent"
			/>
			<Textarea
				label={t("pages.graphWorkflows.config.instructions", "Instructions")}
				value={node.instructions}
				autosize={true}
				minRows={3}
				maxRows={12}
				disabled={readOnly}
				error={errorFor("instructions")}
				onBlur={() => onTouch("instructions")}
				onChange={(event) => onChange({ instructions: event.currentTarget.value })}
				data-testid="gw-node-config-instructions"
			/>
			<Group grow={true} align="flex-start" wrap="wrap">
				<Select
					label={t("pages.graphWorkflows.config.model", "Model")}
					placeholder={t("pages.graphWorkflows.config.modelPlaceholder", "The agent's own model")}
					data={withCurrentValue(modelOptions, node.model)}
					value={node.model}
					clearable={true}
					searchable={true}
					disabled={readOnly}
					onChange={(value) => onChange({ model: value })}
					data-testid="gw-node-config-model"
				/>
				<Select
					label={t("pages.graphWorkflows.config.reasoningEffort", "Reasoning effort")}
					placeholder={t("pages.graphWorkflows.config.reasoningEffortPlaceholder", "Provider default")}
					data={reasoningEfforts.map((effort) => ({
						value: effort,
						label: t(`pages.graphWorkflows.config.effort.${effort}`, effort),
					}))}
					value={node.reasoningEffort}
					clearable={true}
					disabled={readOnly}
					onChange={(value) => onChange({ reasoningEffort: value })}
					data-testid="gw-node-config-effort"
				/>
			</Group>
			<GraphWorkflowJsonField
				label={t("pages.graphWorkflows.config.responseJsonSchema", "Response JSON schema")}
				value={node.responseJsonSchema}
				error={errorFor("responseJsonSchema")}
				readOnly={readOnly}
				onChange={(next) => {
					onTouch("responseJsonSchema");
					onChange({ responseJsonSchema: next });
				}}
				data-testid="gw-node-config-response-schema"
			/>
			<Switch
				label={t("pages.graphWorkflows.config.includeUpstreamOutputs", "Include upstream outputs")}
				description={t(
					"pages.graphWorkflows.config.includeUpstreamOutputsHelp",
					"Hands this agent what the nodes before it produced, on top of the run input.",
				)}
				checked={node.includeUpstreamOutputs}
				disabled={readOnly}
				onChange={(event) => onChange({ includeUpstreamOutputs: event.currentTarget.checked })}
				data-testid="gw-node-config-include-upstream"
			/>
		</>
	);
}
