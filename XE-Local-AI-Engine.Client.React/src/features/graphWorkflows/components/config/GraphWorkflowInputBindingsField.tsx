// The named-value list an LlmCall and a DecisionModel node share: rows of `name → dot path`, sent as a JSON data block
// beside the prompt. Split out so the two forms cannot drift on how a binding is authored.

import { ActionIcon, Button, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { GraphWorkflowInputBinding } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";

function patchBinding(bindings: readonly GraphWorkflowInputBinding[], index: number, patch: Partial<GraphWorkflowInputBinding>) {
	return bindings.map((binding, candidate) => (candidate === index ? { ...binding, ...patch } : binding));
}

export function GraphWorkflowInputBindingsField({
	bindings,
	onChange,
	error,
	onTouch,
	readOnly = false,
}: {
	readonly bindings: readonly GraphWorkflowInputBinding[];
	readonly onChange: (next: readonly GraphWorkflowInputBinding[]) => void;
	readonly error: string | undefined;
	readonly onTouch: () => void;
	readonly readOnly?: boolean;
}) {
	const { t } = useTranslation();
	return (
		<Stack gap="xs">
			<Group justify="space-between">
				<Text size="sm" fw={500}>
					{t("pages.graphWorkflows.config.inputBindings", "Input bindings")}
				</Text>
				<Button
					size="xs"
					variant="light"
					leftSection={<IconPlus size={14} />}
					disabled={readOnly}
					onClick={() => onChange([...bindings, { parameter: "", path: "" }])}
					data-testid="gw-node-config-input-binding-add"
				>
					{t("pages.graphWorkflows.config.addBinding", "Add binding")}
				</Button>
			</Group>
			<Text size="xs" c="dimmed">
				{t(
					"pages.graphWorkflows.config.inputBindingsHelp",
					"Named values are sent as a JSON data block beside the prompt. They do not replace text in the prompt.",
				)}
			</Text>
			{bindings.map((binding, index) => (
				// biome-ignore lint/suspicious/noArrayIndexKey: rows are appended and removed, never reordered; every input is controlled.
				<Group key={`binding-${index}`} gap="xs" align="flex-end" wrap="nowrap">
					<TextInput
						label={t("pages.graphWorkflows.config.bindingName", "Name")}
						value={binding.parameter}
						disabled={readOnly}
						onChange={(event) => onChange(patchBinding(bindings, index, { parameter: event.currentTarget.value }))}
						data-testid={`gw-node-config-input-binding-name-${index}`}
					/>
					<TextInput
						label={t("pages.graphWorkflows.config.bindingPath", "Path")}
						value={binding.path}
						disabled={readOnly}
						onBlur={onTouch}
						onChange={(event) => onChange(patchBinding(bindings, index, { path: event.currentTarget.value }))}
						data-testid={`gw-node-config-input-binding-path-${index}`}
					/>
					<ActionIcon
						color="red"
						variant="subtle"
						aria-label={t("pages.graphWorkflows.config.removeBinding", "Remove binding")}
						disabled={readOnly}
						onClick={() => onChange(bindings.filter((_, candidate) => candidate !== index))}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
			{error ? (
				<Text size="xs" c="red">
					{error}
				</Text>
			) : null}
		</Stack>
	);
}
