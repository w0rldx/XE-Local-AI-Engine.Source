// The short node bodies: Start, Condition, Pause, End and ChatInput. Same shape as the other forms in this folder: they
// own no state, and the panel holds the touched-field bookkeeping that decides when a Zod message is shown. Start's
// fields report a touch in `onChange`, the text fields of the others in `onBlur`.

import { Checkbox, Group, Switch, TagsInput, Textarea, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { GraphWorkflowJsonField } from "@/features/graphWorkflows/components/config/GraphWorkflowJsonField";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	type GraphWorkflowDecisionKind,
	graphWorkflowPauseDecisionKinds,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

interface GraphWorkflowSimpleConfigFormProps<K extends GraphWorkflowCanvasNodeData["kind"]> {
	readonly node: Extract<GraphWorkflowCanvasNodeData, { kind: K }>;
	readonly onChange: (patch: Partial<Extract<GraphWorkflowCanvasNodeData, { kind: K }>>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	readonly readOnly?: boolean;
}

export function GraphWorkflowStartConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	readOnly = false,
}: GraphWorkflowSimpleConfigFormProps<"Start">) {
	const { t } = useTranslation();
	return (
		<>
			<GraphWorkflowJsonField
				label={t("pages.graphWorkflows.config.inputSchema", "Input schema (JSON)")}
				value={node.inputSchema}
				error={errorFor("inputSchema")}
				readOnly={readOnly}
				onChange={(next) => {
					onTouch("inputSchema");
					onChange({ inputSchema: next });
				}}
				data-testid="gw-node-config-input-schema"
			/>
			<GraphWorkflowJsonField
				label={t("pages.graphWorkflows.config.defaultInput", "Default input (JSON)")}
				value={node.defaultInput}
				error={errorFor("defaultInput")}
				readOnly={readOnly}
				onChange={(next) => {
					onTouch("defaultInput");
					onChange({ defaultInput: next });
				}}
				data-testid="gw-node-config-default-input"
			/>
			<TagsInput
				label={t("pages.graphWorkflows.config.aliases", "Aliases")}
				description={t(
					"pages.graphWorkflows.config.aliasesHelp",
					"Phrases that start this workflow from a spoken or typed command, matched ignoring case. Press Enter after each.",
				)}
				value={[...node.aliases]}
				error={errorFor("aliases")}
				disabled={readOnly}
				onChange={(next) => {
					onTouch("aliases");
					onChange({ aliases: next });
				}}
				data-testid="gw-node-config-aliases"
			/>
		</>
	);
}

export function GraphWorkflowConditionConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	readOnly = false,
}: GraphWorkflowSimpleConfigFormProps<"Condition">) {
	const { t } = useTranslation();
	return (
		<TextInput
			label={t("pages.graphWorkflows.config.path", "Path")}
			description={t(
				"pages.graphWorkflows.config.conditionPathHelp",
				"Outgoing edges with no path of their own compare this one.",
			)}
			placeholder="output.json.status"
			value={node.path ?? ""}
			disabled={readOnly}
			error={errorFor("path")}
			onBlur={() => onTouch("path")}
			onChange={(event) => onChange({ path: event.currentTarget.value })}
			data-testid="gw-node-config-path"
		/>
	);
}

export function GraphWorkflowPauseConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	readOnly = false,
}: GraphWorkflowSimpleConfigFormProps<"Pause">) {
	const { t } = useTranslation();
	return (
		<>
			<Textarea
				label={t("pages.graphWorkflows.config.prompt", "Question")}
				value={node.prompt}
				autosize={true}
				minRows={2}
				maxRows={8}
				disabled={readOnly}
				error={errorFor("prompt")}
				onBlur={() => onTouch("prompt")}
				onChange={(event) => onChange({ prompt: event.currentTarget.value })}
				data-testid="gw-node-config-prompt"
			/>
			<Checkbox.Group
				label={t("pages.graphWorkflows.config.allowedDecisions", "Offered decisions")}
				value={[...node.allowedDecisions]}
				error={errorFor("allowedDecisions")}
				onChange={(values) => {
					onTouch("allowedDecisions");
					onChange({ allowedDecisions: toGraphWorkflowDecisionKinds(values) });
				}}
				data-testid="gw-node-config-decisions"
			>
				<Group gap="md" mt={6}>
					{graphWorkflowPauseDecisionKinds.map((decision: GraphWorkflowDecisionKind) => (
						<Checkbox
							key={decision}
							value={decision}
							disabled={readOnly}
							label={t(`pages.graphWorkflows.decision.${decision}`, decision)}
							data-testid={`gw-node-config-decision-${decision}`}
						/>
					))}
				</Group>
			</Checkbox.Group>
			<Switch
				label={t("pages.graphWorkflows.config.requireComment", "A comment is required")}
				checked={node.requireComment}
				disabled={readOnly}
				onChange={(event) => onChange({ requireComment: event.currentTarget.checked })}
				data-testid="gw-node-config-require-comment"
			/>
		</>
	);
}

export function GraphWorkflowEndConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	readOnly = false,
}: GraphWorkflowSimpleConfigFormProps<"End">) {
	const { t } = useTranslation();
	return (
		<>
			<TextInput
				label={t("pages.graphWorkflows.config.outcome", "Outcome")}
				value={node.outcome}
				disabled={readOnly}
				error={errorFor("outcome")}
				onBlur={() => onTouch("outcome")}
				onChange={(event) => onChange({ outcome: event.currentTarget.value })}
				data-testid="gw-node-config-outcome"
			/>
			<TextInput
				label={t("pages.graphWorkflows.config.resultPath", "Result path")}
				placeholder={t("pages.graphWorkflows.config.resultPathPlaceholder", "The whole document")}
				value={node.resultPath ?? ""}
				disabled={readOnly}
				error={errorFor("resultPath")}
				onBlur={() => onTouch("resultPath")}
				onChange={(event) => onChange({ resultPath: event.currentTarget.value })}
				data-testid="gw-node-config-result-path"
			/>
		</>
	);
}

export function GraphWorkflowChatInputConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	readOnly = false,
}: GraphWorkflowSimpleConfigFormProps<"ChatInput">) {
	const { t } = useTranslation();
	return (
		<Textarea
			label={t("pages.graphWorkflows.config.chatInputPrompt", "What to ask the user")}
			description={t(
				"pages.graphWorkflows.config.chatInputPromptHelp",
				"The run waits here until the user answers in the conversation.",
			)}
			value={node.prompt}
			autosize={true}
			minRows={2}
			maxRows={8}
			disabled={readOnly}
			error={errorFor("prompt")}
			onBlur={() => onTouch("prompt")}
			onChange={(event) => onChange({ prompt: event.currentTarget.value })}
			data-testid="gw-node-config-chat-prompt"
		/>
	);
}
