// The DecisionModel node's body: a question, the labels the model must pick from, and the same model picker and input
// bindings an LlmCall node has — the runtime lowers this node to exactly such a call, constrained to the labels.

import { ActionIcon, Button, Group, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { GraphWorkflowInputBindingsField } from "@/features/graphWorkflows/components/config/GraphWorkflowInputBindingsField";
import { withCurrentValue } from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { graphWorkflowDecisionProviders } from "@/features/graphWorkflows/models/GraphWorkflowModels";

type DecisionModelNodeData = Extract<GraphWorkflowCanvasNodeData, { kind: "DecisionModel" }>;

export function GraphWorkflowDecisionModelConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	modelOptions,
	readOnly = false,
}: {
	readonly node: DecisionModelNodeData;
	readonly onChange: (patch: Partial<DecisionModelNodeData>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	readonly modelOptions: readonly { readonly value: string; readonly label: string }[];
	readonly readOnly?: boolean;
}) {
	const { t } = useTranslation();
	const setLabel = (index: number, value: string): void => {
		onTouch("labels");
		onChange({ labels: node.labels.map((label, candidate) => (candidate === index ? value : label)) });
	};

	return (
		<>
			<Textarea
				label={t("pages.graphWorkflows.config.question", "Question")}
				description={t(
					"pages.graphWorkflows.config.questionHelp",
					"What the model decides. Outgoing edges route on output.choice.",
				)}
				value={node.question}
				disabled={readOnly}
				autosize={true}
				minRows={2}
				error={errorFor("question")}
				onBlur={() => onTouch("question")}
				onChange={(event) => onChange({ question: event.currentTarget.value })}
				data-testid="gw-node-config-question"
			/>
			<Stack gap="xs" role="group" aria-labelledby="gw-node-config-labels-title">
				<Group justify="space-between">
					<Text size="sm" fw={500} id="gw-node-config-labels-title">
						{t("pages.graphWorkflows.config.labels", "Labels")}
					</Text>
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlus size={14} />}
						disabled={readOnly}
						onClick={() => onChange({ labels: [...node.labels, ""] })}
						data-testid="gw-node-config-label-add"
					>
						{t("pages.graphWorkflows.config.addLabel", "Add label")}
					</Button>
				</Group>
				<Text size="xs" c="dimmed">
					{t("pages.graphWorkflows.config.labelsHelp", "Between 2 and 32 distinct labels, each up to 64 characters.")}
				</Text>
				{node.labels.map((label, index) => (
					// biome-ignore lint/suspicious/noArrayIndexKey: rows are appended and removed, never reordered; every input is controlled.
					<Group key={`label-${index}`} gap="xs" wrap="nowrap">
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
							onClick={() => {
								onTouch("labels");
								onChange({ labels: node.labels.filter((_, candidate) => candidate !== index) });
							}}
							data-testid={`gw-node-config-label-remove-${index}`}
						>
							<IconTrash size={16} />
						</ActionIcon>
					</Group>
				))}
				{errorFor("labels") ? (
					<Text size="xs" c="red" data-testid="gw-node-config-labels-error">
						{errorFor("labels")}
					</Text>
				) : null}
			</Stack>
			<Select
				label={t("pages.graphWorkflows.config.decisionProvider", "Provider")}
				placeholder={t("pages.graphWorkflows.config.decisionProviderPlaceholder", "Language model (default)")}
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
				onChange={(provider) => onChange({ provider })}
				data-testid="gw-node-config-decision-provider"
			/>
			<Select
				label={t("pages.graphWorkflows.config.model", "Model")}
				placeholder={t("pages.graphWorkflows.config.llmModelPlaceholder", "Node default local model")}
				data={withCurrentValue(modelOptions, node.model)}
				value={node.model}
				clearable={true}
				searchable={true}
				disabled={readOnly}
				onChange={(model) => onChange({ model })}
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
