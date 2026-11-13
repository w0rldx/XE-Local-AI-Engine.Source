// The DevTask node's body: `implement` describes the Development task it creates and drives, `apply` names the
// implement node whose patch it lands. Which node `apply` may name is DEVTASK-1, the server's rule; the picker only
// offers this graph's implement nodes, and the save answers with the server's message for anything else.

import { Select, TagsInput, Textarea, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { GraphWorkflowInputBindingsField } from "@/features/graphWorkflows/components/config/GraphWorkflowInputBindingsField";
import { withCurrentValue } from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { graphWorkflowDevTaskActions } from "@/features/graphWorkflows/models/GraphWorkflowModels";

type DevTaskNodeData = Extract<GraphWorkflowCanvasNodeData, { kind: "DevTask" }>;

export interface GraphWorkflowDevTaskConfigFormProps {
	readonly node: DevTaskNodeData;
	readonly onChange: (patch: Partial<DevTaskNodeData>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	/** Every node of the graph being edited, for the `apply` picker. */
	readonly graphNodes: readonly GraphWorkflowCanvasNodeData[];
	readonly readOnly?: boolean;
}

export function GraphWorkflowDevTaskConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	graphNodes,
	readOnly = false,
}: GraphWorkflowDevTaskConfigFormProps) {
	const { t } = useTranslation();
	const implementOptions = graphNodes.flatMap((candidate) =>
		candidate.kind === "DevTask" && candidate.action === "implement" && candidate.key !== node.key
			? [
					{
						value: candidate.key,
						label: candidate.label.trim().length > 0 ? `${candidate.label} (${candidate.key})` : candidate.key,
					},
				]
			: [],
	);

	return (
		<>
			<Select
				label={t("pages.graphWorkflows.config.devTaskAction", "Action")}
				data={withCurrentValue(
					graphWorkflowDevTaskActions.map((action) => ({
						value: action,
						label: t(`pages.graphWorkflows.config.devTaskActions.${action}`, action),
					})),
					node.action,
				)}
				value={node.action}
				allowDeselect={false}
				disabled={readOnly}
				onChange={(value) => onChange({ action: value ?? "implement" })}
				data-testid="gw-node-config-devtask-action"
			/>
			{node.action === "apply" ? (
				<Select
					label={t("pages.graphWorkflows.config.devTaskTask", "Implement node to apply")}
					description={t(
						"pages.graphWorkflows.config.devTaskTaskHelp",
						"Lands the patch of that node's Development task. Every edge into this node must be a Pause's Approve.",
					)}
					placeholder={t("pages.graphWorkflows.config.devTaskTaskPlaceholder", "Pick an implement node")}
					data={withCurrentValue(implementOptions, node.task)}
					value={node.task}
					disabled={readOnly}
					error={errorFor("task")}
					onBlur={() => onTouch("task")}
					onChange={(value) => {
						onTouch("task");
						onChange({ task: value });
					}}
					data-testid="gw-node-config-devtask-task"
				/>
			) : (
				<>
					<TextInput
						label={t("pages.graphWorkflows.config.devTaskTitle", "Task title")}
						placeholder={t("pages.graphWorkflows.config.devTaskTitlePlaceholder", "The node label")}
						value={node.title}
						disabled={readOnly}
						onChange={(event) => onChange({ title: event.currentTarget.value })}
						data-testid="gw-node-config-devtask-title"
					/>
					<Textarea
						label={t("pages.graphWorkflows.config.devTaskRequirements", "Requirements")}
						description={t(
							"pages.graphWorkflows.config.devTaskRequirementsHelp",
							"What the coder implements in the run's Development project, which the run input names as developmentProjectId.",
						)}
						value={node.requirements}
						autosize={true}
						minRows={3}
						maxRows={12}
						disabled={readOnly}
						error={errorFor("requirements")}
						onBlur={() => onTouch("requirements")}
						onChange={(event) => onChange({ requirements: event.currentTarget.value })}
						data-testid="gw-node-config-devtask-requirements"
					/>
					<TagsInput
						label={t("pages.graphWorkflows.config.devTaskAcceptanceCriteria", "Acceptance criteria")}
						description={t(
							"pages.graphWorkflows.config.devTaskAcceptanceCriteriaHelp",
							"Press Enter after each. Left empty, the project's first task supplies them.",
						)}
						// A criterion is a sentence, so a comma must not split it.
						splitChars={[]}
						value={[...node.acceptanceCriteria]}
						disabled={readOnly}
						onChange={(next) => onChange({ acceptanceCriteria: next })}
						data-testid="gw-node-config-devtask-criteria"
					/>
					<Textarea
						label={t("pages.graphWorkflows.config.devTaskInstructions", "Instructions")}
						description={t(
							"pages.graphWorkflows.config.devTaskInstructionsHelp",
							"Policy text recorded on the task for its review rounds.",
						)}
						value={node.instructions}
						autosize={true}
						minRows={2}
						maxRows={8}
						disabled={readOnly}
						onChange={(event) => onChange({ instructions: event.currentTarget.value })}
						data-testid="gw-node-config-devtask-instructions"
					/>
					<GraphWorkflowInputBindingsField
						bindings={node.inputBindings}
						onChange={(next) => onChange({ inputBindings: next })}
						error={errorFor("inputBindings")}
						onTouch={() => onTouch("inputBindings")}
						readOnly={readOnly}
					/>
				</>
			)}
		</>
	);
}
