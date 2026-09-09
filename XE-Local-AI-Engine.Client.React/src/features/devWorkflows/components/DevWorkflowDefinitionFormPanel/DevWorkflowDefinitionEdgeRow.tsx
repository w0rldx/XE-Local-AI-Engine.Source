import { ActionIcon, Select, Table, TextInput } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { parseConditionValue, readValue } from "@/features/devWorkflows/models/DevWorkflowDefinitionFormHelpers";
import type { DevWorkflowGraphEdge } from "@/features/devWorkflows/models/DevWorkflowModels";

/**
 * `DevWorkflowConditionOperator`, in the lowercase spelling the seeded templates use. The server parses it
 * case-insensitively and REFUSES anything outside the set, so this is a picker for the same reason `nodeTypes` is: a
 * token nothing parses is a definition whose routing nobody can predict, caught at save rather than at run start.
 */
const conditionOperators = ["eq", "ne", "gt", "gte", "lt", "lte", "exists", "notExists"] as const;

export function DevWorkflowDefinitionEdgeRow({
	edge,
	index,
	nodeKeyOptions,
	onPatch,
	onRemove,
}: {
	readonly edge: DevWorkflowGraphEdge;
	readonly index: number;
	readonly nodeKeyOptions: readonly string[];
	readonly onPatch: (patch: Partial<DevWorkflowGraphEdge>) => void;
	readonly onRemove: () => void;
}) {
	const { t } = useTranslation();
	const condition = edge.condition ?? undefined;

	/**
	 * The condition is written as a whole or not at all: an edge carrying a path with no operator is a rule the
	 * runtime cannot evaluate, and clearing the path is how an operator says "always taken".
	 *
	 * `value` is only rewritten when the VALUE cell was the one edited. It is scalar JSON on the wire and the server
	 * compares by JSON kind — `Compare` answers null on any type mismatch and the edge then silently never fires — so a
	 * stored `true` must not become `"true"` because someone corrected the path beside it.
	 */
	const patchCondition = (patch: { path?: string; op?: string; value?: string }): void => {
		const path = patch.path ?? condition?.path ?? "";
		if (path.length === 0) {
			onPatch({ condition: null });
			return;
		}
		const value = "value" in patch ? parseConditionValue(patch.value ?? "") : (condition?.value ?? "");
		onPatch({ condition: { path, op: patch.op ?? condition?.op ?? "", value } });
	};

	return (
		<Table.Tr data-testid={`dev-workflow-definition-edge-${index}`}>
			<Table.Td>
				<Select
					aria-label={t("pages.devWorkflows.definition.edgeFrom", "From")}
					data={[...nodeKeyOptions]}
					value={edge.from ?? null}
					onChange={(value) => onPatch({ from: value ?? "" })}
					data-testid={`dev-workflow-definition-edge-from-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<Select
					aria-label={t("pages.devWorkflows.definition.edgeTo", "To")}
					data={[...nodeKeyOptions]}
					value={edge.to ?? null}
					onChange={(value) => onPatch({ to: value ?? "" })}
					data-testid={`dev-workflow-definition-edge-to-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<TextInput
					aria-label={t("pages.devWorkflows.definition.conditionPath", "Condition path")}
					value={condition?.path ?? ""}
					onChange={(event) => patchCondition({ path: event.currentTarget.value })}
					data-testid={`dev-workflow-definition-edge-path-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				{/* The stored operator is unioned in so a definition authored by hand in another casing still shows
				    what it says and round-trips, instead of rendering as an empty picker. */}
				<Select
					aria-label={t("pages.devWorkflows.definition.conditionOp", "Operator")}
					data={[...new Set<string>([...conditionOperators, ...(condition?.op ? [condition.op] : [])])]}
					value={condition?.op ?? null}
					onChange={(value) => patchCondition({ op: value ?? "" })}
					data-testid={`dev-workflow-definition-edge-op-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<TextInput
					aria-label={t("pages.devWorkflows.definition.conditionValue", "Value")}
					value={readValue(condition?.value)}
					onChange={(event) => patchCondition({ value: event.currentTarget.value })}
					data-testid={`dev-workflow-definition-edge-value-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<ActionIcon
					variant="subtle"
					color="red"
					aria-label={t("pages.devWorkflows.definition.removeEdge", "Remove edge")}
					onClick={onRemove}
					data-testid={`dev-workflow-definition-edge-remove-${index}`}
				>
					<IconTrash size={16} />
				</ActionIcon>
			</Table.Td>
		</Table.Tr>
	);
}
