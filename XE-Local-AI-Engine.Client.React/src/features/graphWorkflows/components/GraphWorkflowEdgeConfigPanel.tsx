// The selected edge's label and its optional condition. Same shape as the node panel: a prop-driven `Stack`, no query,
// no graph state — the page hands it the edge and forwards its patches to the editor hook.
//
// The one thing this panel exists to make visible is INHERITANCE (ruling C2): an edge with no path of its own resolves
// the path of its source `Condition` node, so an edge that looks pathless still validates. Showing the inherited path
// greyed out, with the node it came from, is what stops that reading as a bug.

import { Alert, Button, Group, Select, Stack, Switch, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type {
	GraphWorkflowCanvasEdge,
	GraphWorkflowCanvasEdgeCondition,
	GraphWorkflowCanvasNodeData,
	GraphWorkflowEdgeData,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	type GraphWorkflowConditionOperator,
	graphWorkflowConditionOperators,
	normalizeGraphWorkflowConditionOperator,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { edgeConditionSchema, type GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

export interface GraphWorkflowEdgeConfigPanelProps {
	readonly edge: GraphWorkflowCanvasEdge;
	/** The edge's SOURCE node, for the inherited Condition path. Absent when the graph is mid-edit. */
	readonly sourceNode: GraphWorkflowCanvasNodeData | undefined;
	/** Only the issues whose `subject` is this edge's id — the page filters. */
	readonly issues: readonly GraphWorkflowGraphIssue[];
	readonly onChange: (patch: Partial<GraphWorkflowEdgeData>) => void;
	readonly onRemove: () => void;
	readonly readOnly?: boolean;
}

/** `Exists`/`NotExists` take no operand, so the value field is not merely disabled — it is not there. */
function takesValue(op: GraphWorkflowConditionOperator): boolean {
	return op !== "Exists" && op !== "NotExists";
}

export function GraphWorkflowEdgeConfigPanel({
	edge,
	sourceNode,
	issues,
	onChange,
	onRemove,
	readOnly = false,
}: GraphWorkflowEdgeConfigPanelProps) {
	const { t } = useTranslation();
	const [touched, setTouched] = useState<readonly string[]>([]);
	const condition = edge.data?.condition;
	const inheritedPath = sourceNode?.kind === "Condition" ? (sourceNode.path ?? "") : "";

	const result = condition ? edgeConditionSchema.safeParse(condition) : undefined;
	const errorFor = (field: string): string | undefined => {
		if (result === undefined || result.success || !touched.includes(field)) {
			return undefined;
		}
		const issue = result.error.issues.find((candidate) => String(candidate.path[0] ?? "") === field);
		return issue ? t(issue.message) : undefined;
	};
	const touch = (field: string): void => setTouched((current) => (current.includes(field) ? current : [...current, field]));

	const patchCondition = (patch: Partial<GraphWorkflowCanvasEdgeCondition>): void =>
		onChange({ condition: { op: "Eq", value: "", ...condition, ...patch } });

	return (
		<Stack gap="sm" data-testid="gw-edge-config">
			<Group justify="space-between" wrap="nowrap" align="center">
				<Text fw={600} data-testid="gw-edge-config-title">
					{t("pages.graphWorkflows.edge.title", "Edge {{key}}", { key: edge.id })}
				</Text>
				<Button
					size="xs"
					variant="light"
					color="red"
					leftSection={<IconTrash size={14} />}
					disabled={readOnly}
					onClick={onRemove}
					data-testid="gw-edge-config-remove"
				>
					{t("pages.graphWorkflows.edge.remove", "Delete edge")}
				</Button>
			</Group>

			{issues.length > 0 ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="gw-edge-config-issues">
					<Stack gap={4}>
						{issues.map((issue) => (
							<Text key={`${issue.rule}:${issue.subject ?? ""}`} size="sm">
								{issue.message ??
									t(`pages.graphWorkflows.definition.issues.${issue.rule}`, issue.rule, { subject: issue.subject ?? "" })}
							</Text>
						))}
					</Stack>
				</Alert>
			) : null}

			<Text size="xs" c="dimmed" data-testid="gw-edge-config-endpoints">
				{t("pages.graphWorkflows.edge.endpoints", "{{from}} → {{to}}", { from: edge.source, to: edge.target })}
			</Text>

			<TextInput
				label={t("pages.graphWorkflows.edge.label", "Label")}
				placeholder={t("pages.graphWorkflows.edge.labelPlaceholder", "Shown on the canvas")}
				value={edge.data?.label ?? ""}
				disabled={readOnly}
				onChange={(event) => onChange({ label: event.currentTarget.value })}
				data-testid="gw-edge-config-label"
			/>

			<Switch
				label={t("pages.graphWorkflows.edge.conditional", "Only follow this edge when a comparison holds")}
				checked={condition !== undefined}
				disabled={readOnly}
				onChange={(event) => {
					// Switching it OFF clears the condition outright rather than leaving a disabled one behind: an edge
					// that still carries `op`/`value` would keep branching on the next save.
					const enabled = event.currentTarget.checked;
					onChange(enabled ? { condition: { op: "Eq", value: "" } } : { condition: undefined });
				}}
				data-testid="gw-edge-config-conditional"
			/>

			{condition ? (
				<>
					<TextInput
						label={t("pages.graphWorkflows.edge.path", "Path")}
						placeholder={inheritedPath.length > 0 ? inheritedPath : "output.json.status"}
						description={
							(condition.path ?? "").length === 0 && inheritedPath.length > 0
								? t("pages.graphWorkflows.edge.pathInherited", "Inherits “{{path}}” from {{node}}", {
										path: inheritedPath,
										node: sourceNode?.key ?? "",
									})
								: undefined
						}
						value={condition.path ?? ""}
						disabled={readOnly}
						error={errorFor("path")}
						onBlur={() => touch("path")}
						onChange={(event) => patchCondition({ path: event.currentTarget.value })}
						data-testid="gw-edge-config-path"
					/>
					<Select
						label={t("pages.graphWorkflows.edge.operator", "Comparison")}
						data={graphWorkflowConditionOperators.map((operator) => ({
							value: operator,
							label: t(`pages.graphWorkflows.conditionOperator.${operator}`, operator),
						}))}
						value={normalizeGraphWorkflowConditionOperator(condition.op) ?? "Eq"}
						allowDeselect={false}
						disabled={readOnly}
						onChange={(value) => patchCondition({ op: normalizeGraphWorkflowConditionOperator(value) ?? "Eq" })}
						data-testid="gw-edge-config-operator"
					/>
					{takesValue(condition.op) ? (
						<TextInput
							label={t("pages.graphWorkflows.edge.value", "Value")}
							description={t("pages.graphWorkflows.edge.valueHelp", "Read as JSON when it parses, otherwise as text.")}
							value={condition.value}
							disabled={readOnly}
							error={errorFor("value")}
							onBlur={() => touch("value")}
							onChange={(event) => patchCondition({ value: event.currentTarget.value })}
							data-testid="gw-edge-config-value"
						/>
					) : null}
				</>
			) : null}
		</Stack>
	);
}
