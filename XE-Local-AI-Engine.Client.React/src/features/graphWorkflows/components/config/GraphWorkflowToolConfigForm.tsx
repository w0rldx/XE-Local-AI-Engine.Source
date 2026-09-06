// The Tool node's body, and the one place the two shape conversions the plan names are visible to an operator: the
// wire's `arguments` OBJECT is edited as text here, and the wire's `argumentBindings` MAP is edited as ordered rows.
// `canvasToGraph` owns both conversions; this file only makes the tool's own parameter schema drive what the operator
// can pick, so a binding names a real parameter rather than a typed guess.

import { ActionIcon, Button, Group, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { GraphWorkflowJsonField } from "@/features/graphWorkflows/components/config/GraphWorkflowJsonField";
import type {
	GraphWorkflowArgumentBinding,
	GraphWorkflowCanvasNodeData,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowToolResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";

type ToolNodeData = Extract<GraphWorkflowCanvasNodeData, { kind: "Tool" }>;

/** The `properties` of a tool's raw JSON-schema TEXT. Unparseable text yields none rather than throwing at render. */
function schemaProperties(parameterSchema: string | undefined): readonly { readonly name: string; readonly type: string }[] {
	try {
		const parsed: unknown = JSON.parse(parameterSchema ?? "");
		const properties = (parsed as { properties?: unknown } | null)?.properties;
		if (typeof properties !== "object" || properties === null || Array.isArray(properties)) {
			return [];
		}
		return Object.entries(properties).map(([name, value]) => ({
			name,
			type: typeof (value as { type?: unknown } | null)?.type === "string" ? String((value as { type: string }).type) : "",
		}));
	} catch {
		return [];
	}
}

function emptyForType(type: string): unknown {
	switch (type) {
		case "string":
			return "";
		case "number":
		case "integer":
			return 0;
		case "boolean":
			return false;
		case "array":
			return [];
		case "object":
			return {};
		default:
			return null;
	}
}

/** An empty-object template for a tool's arguments, so an operator edits real parameter names rather than typing them. */
function argumentsTemplate(tool: GraphWorkflowToolResponse | undefined): string {
	const properties = schemaProperties(tool?.parameterSchema);
	if (properties.length === 0) {
		return "";
	}
	return JSON.stringify(Object.fromEntries(properties.map((property) => [property.name, emptyForType(property.type)])), null, 2);
}

/** One binding row patched in place, keeping the array's order — which is what the wire map's insertion order becomes. */
function patchBinding(
	bindings: readonly GraphWorkflowArgumentBinding[],
	index: number,
	patch: Partial<GraphWorkflowArgumentBinding>,
): readonly GraphWorkflowArgumentBinding[] {
	return bindings.map((binding, candidate) => (candidate === index ? { ...binding, ...patch } : binding));
}

export interface GraphWorkflowToolConfigFormProps {
	readonly node: ToolNodeData;
	readonly onChange: (patch: Partial<ToolNodeData>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	readonly tools: readonly GraphWorkflowToolResponse[];
	readonly readOnly?: boolean;
}

export function GraphWorkflowToolConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	tools,
	readOnly = false,
}: GraphWorkflowToolConfigFormProps) {
	const { t } = useTranslation();
	const selectedTool = tools.find((tool) => tool.name === node.toolName);
	const properties = schemaProperties(selectedTool?.parameterSchema);

	return (
		<>
			<Select
				label={t("pages.graphWorkflows.config.tool", "Tool")}
				placeholder={t("pages.graphWorkflows.config.toolPlaceholder", "Choose a tool")}
				// Exactly the list the page was given: `GET graph-workflows/tools` is already filtered server-side to what
				// a graph node may run, and re-deriving that here would be a second rule that drifts.
				data={tools.map((tool) => ({ value: tool.name ?? "", label: tool.name ?? "" }))}
				value={node.toolName}
				searchable={true}
				disabled={readOnly}
				error={errorFor("toolName")}
				onBlur={() => onTouch("toolName")}
				onChange={(value) => {
					const next = tools.find((tool) => tool.name === value);
					const current = node.argumentsJson.trim();
					// Seeded only when the operator has typed nothing of their own — an empty box, or the template the
					// PREVIOUS tool put there. Anything else is theirs and survives the swap.
					const seed = current.length === 0 || current === argumentsTemplate(selectedTool).trim();
					onChange({ toolName: value, ...(seed ? { argumentsJson: argumentsTemplate(next) } : {}) });
				}}
				data-testid="gw-node-config-tool"
			/>
			{selectedTool?.description ? (
				<Text size="xs" c="dimmed" data-testid="gw-node-config-tool-description">
					{selectedTool.description}
				</Text>
			) : null}
			<GraphWorkflowJsonField
				label={t("pages.graphWorkflows.config.arguments", "Arguments (JSON)")}
				value={node.argumentsJson}
				error={errorFor("argumentsJson")}
				readOnly={readOnly}
				onChange={(next) => {
					onTouch("argumentsJson");
					onChange({ argumentsJson: next });
				}}
				data-testid="gw-node-config-arguments"
			/>
			<Stack gap="xs">
				<Group justify="space-between" wrap="wrap">
					<Text size="sm" fw={500}>
						{t("pages.graphWorkflows.config.argumentBindings", "Argument bindings")}
					</Text>
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlus size={14} />}
						disabled={readOnly}
						onClick={() => onChange({ argumentBindings: [...node.argumentBindings, { parameter: "", path: "" }] })}
						data-testid="gw-node-config-binding-add"
					>
						{t("pages.graphWorkflows.config.addBinding", "Add binding")}
					</Button>
				</Group>
				<Text size="xs" c="dimmed">
					{t(
						"pages.graphWorkflows.config.argumentBindingsHelp",
						"Replaces one argument with a value read from the run document at save time.",
					)}
				</Text>
				{node.argumentBindings.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="gw-node-config-no-bindings">
						{t("pages.graphWorkflows.config.noBindings", "None. The arguments above are sent as they are.")}
					</Text>
				) : (
					node.argumentBindings.map((binding, index) => (
						<Group
							// The parameter name is the field BEING edited, so it cannot be the key: it would remount the input on
							// every keystroke and take the caret with it. Every input below is fully controlled by its prop, so a
							// shifted index after a remove still renders the right values.
							// biome-ignore lint/suspicious/noArrayIndexKey: rows are appended and removed, never reordered.
							key={`binding-${index}`}
							gap="xs"
							align="flex-end"
							wrap="nowrap"
						>
							<Select
								label={t("pages.graphWorkflows.config.bindingParameter", "Parameter")}
								data={properties.map((property) => ({ value: property.name, label: property.name }))}
								value={binding.parameter.length > 0 ? binding.parameter : null}
								searchable={true}
								disabled={readOnly}
								style={{ flex: 1, minWidth: 0 }}
								onChange={(value) =>
									onChange({ argumentBindings: patchBinding(node.argumentBindings, index, { parameter: value ?? "" }) })
								}
								data-testid={`gw-node-config-binding-parameter-${index}`}
							/>
							<TextInput
								label={t("pages.graphWorkflows.config.bindingPath", "Path")}
								value={binding.path}
								disabled={readOnly}
								style={{ flex: 1, minWidth: 0 }}
								onChange={(event) =>
									onChange({ argumentBindings: patchBinding(node.argumentBindings, index, { path: event.currentTarget.value }) })
								}
								data-testid={`gw-node-config-binding-path-${index}`}
							/>
							<ActionIcon
								variant="subtle"
								color="red"
								disabled={readOnly}
								aria-label={t("pages.graphWorkflows.config.removeBinding", "Remove binding")}
								onClick={() =>
									onChange({ argumentBindings: node.argumentBindings.filter((_, candidate) => candidate !== index) })
								}
								data-testid={`gw-node-config-binding-remove-${index}`}
							>
								<IconTrash size={16} />
							</ActionIcon>
						</Group>
					))
				)}
			</Stack>
		</>
	);
}
