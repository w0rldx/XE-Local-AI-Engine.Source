// The DecisionModel node's body: a question, its type, the labels (or score levels) the model must pick from, and a
// provider. `llm` lowers the node to an LlmCall constrained to the labels and answers `choice` only; `systemone` runs a
// native decision model (kind `Decision`) and answers `choice`, `noul` or `score`.

import { ActionIcon, Button, Group, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { GraphWorkflowInputBindingsField } from "@/features/graphWorkflows/components/config/GraphWorkflowInputBindingsField";
import {
	type GraphWorkflowModelSelectOption,
	withCloudModelLabels,
	withCurrentValue,
} from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	graphWorkflowDecisionProviders,
	graphWorkflowDecisionQuestionTypes,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { GRAPH_WORKFLOW_MAX_CRITERION_LENGTH } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

type DecisionModelNodeData = Extract<GraphWorkflowCanvasNodeData, { kind: "DecisionModel" }>;

/**
 * `criteria` with one key renamed (or dropped when `to` is undefined), keeping the other descriptions. A rename onto a
 * label that already has its own description keeps that one: the moved text is dropped rather than overwriting it.
 */
function renameCriterion(
	criteria: Readonly<Record<string, string>>,
	from: string,
	to: string | undefined,
): Readonly<Record<string, string>> {
	const { [from]: description, ...rest } = criteria;
	return to === undefined || description === undefined || Object.hasOwn(rest, to) ? rest : { ...rest, [to]: description };
}

export function GraphWorkflowDecisionModelConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	modelOptions,
	decisionModelOptions = [],
	modelOptionsReady = true,
	decisionModelOptionsReady = true,
	readOnly = false,
}: {
	readonly node: DecisionModelNodeData;
	readonly onChange: (patch: Partial<DecisionModelNodeData>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	readonly modelOptions: readonly GraphWorkflowModelSelectOption[];
	readonly decisionModelOptions?: readonly GraphWorkflowModelSelectOption[];
	/** Whether each list's query succeeded: an empty list from a pending query is not a reason to clear a model. */
	readonly modelOptionsReady?: boolean;
	readonly decisionModelOptionsReady?: boolean;
	readonly readOnly?: boolean;
}) {
	const { t } = useTranslation();
	const type = node.questionType ?? "choice";
	const native = node.provider === "systemone";
	const models = native ? decisionModelOptions : modelOptions;

	const setLabel = (index: number, value: string): void => {
		onTouch("labels");
		const previous = node.labels[index] ?? "";
		onChange({
			labels: node.labels.map((label, candidate) => (candidate === index ? value : label)),
			// A description follows its label, so a rename never leaves a criteria key the parser refuses.
			...(Object.hasOwn(node.criteria, previous) ? { criteria: renameCriterion(node.criteria, previous, value) } : {}),
		});
	};
	const removeLabel = (index: number): void => {
		onTouch("labels");
		const removed = node.labels[index] ?? "";
		onChange({
			labels: node.labels.filter((_, candidate) => candidate !== index),
			...(Object.hasOwn(node.criteria, removed) ? { criteria: renameCriterion(node.criteria, removed, undefined) } : {}),
		});
	};

	return (
		<>
			<Textarea
				label={t("pages.graphWorkflows.config.question", "Question")}
				description={
					type === "noul"
						? t(
								"pages.graphWorkflows.config.questionHelpNoul",
								"A yes/no question. output.noul is P(yes) from 0 to 1: route with Gte on output.noul, or with Eq on output.choice, which is the literal yes or no.",
							)
						: type === "score"
							? t(
									"pages.graphWorkflows.config.questionHelpScore",
									"What the model rates on the levels below. Route with Gte or Lt on output.score.",
								)
							: t("pages.graphWorkflows.config.questionHelp", "What the model decides. Outgoing edges route on output.choice.")
				}
				data-testid="gw-node-config-question"
				value={node.question}
				disabled={readOnly}
				autosize={true}
				minRows={2}
				error={errorFor("question")}
				onBlur={() => onTouch("question")}
				onChange={(event) => onChange({ question: event.currentTarget.value })}
			/>
			<Select
				label={t("pages.graphWorkflows.config.questionType", "Question type")}
				data={withCurrentValue(
					graphWorkflowDecisionQuestionTypes.map((value) => ({
						value,
						label: t(`pages.graphWorkflows.config.questionTypeName.${value}`, value),
					})),
					node.questionType,
				)}
				value={type}
				allowDeselect={false}
				disabled={readOnly}
				error={errorFor("questionType")}
				onChange={(questionType) => {
					onTouch("questionType");
					onChange({ questionType });
				}}
				data-testid="gw-node-config-decision-type"
			/>
			{type === "noul" ? null : (
				<Stack gap="xs" role="group" aria-labelledby="gw-node-config-labels-title">
					<Group justify="space-between">
						<Text size="sm" fw={500} id="gw-node-config-labels-title">
							{type === "score"
								? t("pages.graphWorkflows.config.levels", "Levels, lowest first")
								: t("pages.graphWorkflows.config.labels", "Labels")}
						</Text>
						<Button
							size="xs"
							variant="light"
							leftSection={<IconPlus size={14} />}
							disabled={readOnly}
							onClick={() => onChange({ labels: [...node.labels, ""] })}
							data-testid="gw-node-config-label-add"
						>
							{type === "score"
								? t("pages.graphWorkflows.config.addLevel", "Add level")
								: t("pages.graphWorkflows.config.addLabel", "Add label")}
						</Button>
					</Group>
					<Text size="xs" c="dimmed" data-testid="gw-node-config-labels-help">
						{type === "score"
							? t(
									"pages.graphWorkflows.config.levelsHelp",
									"Between 2 and 10 distinct levels, lowest first. output.score is continuous; output.choice is the nearest level.",
								)
							: t("pages.graphWorkflows.config.labelsHelp", "Between 2 and 32 distinct labels, each up to 64 characters.")}
					</Text>
					{node.labels.map((label, index) => (
						// biome-ignore lint/suspicious/noArrayIndexKey: rows are appended and removed, never reordered; every input is controlled.
						<Stack key={`label-${index}`} gap={4}>
							<Group gap="xs" wrap="nowrap">
								<TextInput
									style={{ flex: 1 }}
									aria-label={t("pages.graphWorkflows.config.labelValue", "Label {{index}}", { index: index + 1 })}
									value={label}
									disabled={readOnly}
									onChange={(event) => setLabel(index, event.currentTarget.value)}
									// The parser keeps a label verbatim, so `coding ` would never match an `output.choice Eq "coding"`
									// edge. Trimmed on blur rather than refused: it is a typing slip, not a rule.
									onBlur={() => {
										if (label.trim() !== label) {
											setLabel(index, label.trim());
										}
									}}
									data-testid={`gw-node-config-label-${index}`}
								/>
								<ActionIcon
									color="red"
									variant="subtle"
									aria-label={t("pages.graphWorkflows.config.removeLabel", "Remove label")}
									disabled={readOnly}
									onClick={() => removeLabel(index)}
									data-testid={`gw-node-config-label-remove-${index}`}
								>
									<IconTrash size={16} />
								</ActionIcon>
							</Group>
							{type === "choice" ? (
								<TextInput
									size="xs"
									maxLength={GRAPH_WORKFLOW_MAX_CRITERION_LENGTH}
									placeholder={t("pages.graphWorkflows.config.criterionPlaceholder", "Optional description of this label")}
									aria-label={t("pages.graphWorkflows.config.criterionValue", "Description of label {{index}}", {
										index: index + 1,
									})}
									value={node.criteria[label] ?? ""}
									disabled={readOnly || label.trim().length === 0}
									onChange={(event) => {
										const description = event.currentTarget.value;
										onChange({
											criteria:
												description.length > 0
													? { ...node.criteria, [label]: description }
													: renameCriterion(node.criteria, label, undefined),
										});
									}}
									data-testid={`gw-node-config-label-criterion-${index}`}
								/>
							) : null}
						</Stack>
					))}
					{errorFor("labels") ? (
						<Text size="xs" c="red" data-testid="gw-node-config-labels-error">
							{errorFor("labels")}
						</Text>
					) : null}
				</Stack>
			)}
			<Select
				label={t("pages.graphWorkflows.config.decisionProvider", "Provider")}
				placeholder={t("pages.graphWorkflows.config.decisionProviderPlaceholder", "Language model (default)")}
				description={
					native
						? t(
								"pages.graphWorkflows.config.decisionProviderHint.systemone",
								"Native decision model; probabilities are model-reported and uncalibrated.",
							)
						: undefined
				}
				data={withCurrentValue(
					graphWorkflowDecisionProviders.map((provider) => ({
						value: provider,
						label: t(`pages.graphWorkflows.config.decisionProviderName.${provider}`, provider),
					})),
					node.provider,
				)}
				value={node.provider}
				clearable={true}
				disabled={readOnly}
				onChange={(provider) => {
					onTouch("questionType");
					// The two providers draw from disjoint model lists (chat vs Decision kind): a model that is not in the
					// new provider's list would only fail at run start. Cleared only on a real llm ↔ systemone switch and
					// only once that list has loaded, so a pending or failed query never erases a valid model.
					const switches = (provider ?? "llm") !== (node.provider ?? "llm");
					const targetSystemOne = provider === "systemone";
					const targetReady = targetSystemOne ? decisionModelOptionsReady : modelOptionsReady;
					const targetModels = targetSystemOne ? decisionModelOptions : modelOptions;
					const clearModel =
						switches && targetReady && node.model !== null && !targetModels.some((option) => option.value === node.model);
					onChange({ provider, ...(clearModel ? { model: null } : {}) });
				}}
				data-testid="gw-node-config-decision-provider"
			/>
			<Select
				label={t("pages.graphWorkflows.config.model", "Model")}
				placeholder={
					native
						? t("pages.graphWorkflows.config.decisionModelPlaceholder", "Pick an installed decision model")
						: t("pages.graphWorkflows.config.llmModelPlaceholder", "Node default local model")
				}
				description={
					native
						? t(
								"pages.graphWorkflows.config.decisionModelHelp",
								"Only installed models of type Decision run here. Import one from Model Management.",
							)
						: t(
								"pages.graphWorkflows.config.cloudModelHelp",
								"Cloud models run only when “Let cloud models run unattended” is on in Node Settings → Privacy & updates, or when the model's external connection grants unattended runs.",
							)
				}
				data={withCurrentValue(withCloudModelLabels(models, t), node.model)}
				value={node.model}
				clearable={true}
				searchable={true}
				disabled={readOnly}
				error={errorFor("model")}
				onChange={(model) => {
					onTouch("model");
					onChange({ model });
				}}
				data-testid="gw-node-config-decision-model"
			/>
			<GraphWorkflowInputBindingsField
				bindings={node.inputBindings}
				onChange={(inputBindings) => onChange({ inputBindings })}
				error={errorFor("inputBindings")}
				onTouch={() => onTouch("inputBindings")}
				readOnly={readOnly}
			/>
		</>
	);
}
