import { useEffect, useMemo, useState } from "react";

import type { XeLocalAiEngineClientEndpointsDevelopmentWorkflowsV1DevWorkflowDefinitionResponse as DevWorkflowDefinitionResponse } from "@/core/api/generated/types.gen";
import { validateDevWorkflowGraph } from "@/features/devWorkflows/models/DevWorkflowDefinitionValidation";
import type {
	DevWorkflowGraph,
	DevWorkflowGraphEdge,
	DevWorkflowGraphNode,
} from "@/features/devWorkflows/models/DevWorkflowModels";

/** One editable row and the id it keeps for as long as this editing session lasts. */
export interface DraftRow<T> {
	readonly id: string;
	readonly value: T;
}

export function toRow<T>(value: T): DraftRow<T> {
	return { id: crypto.randomUUID(), value };
}

/**
 * The definition editor's whole draft: the stored document as editable rows, the graph they add up to, the issues that
 * graph has, and the row edits the form offers. Seeding, the save-error state it resets and the derived graph live
 * together because they are one story — a fresh document replaces every one of them at once.
 */
export function useDevWorkflowDefinitionDraft(definition: DevWorkflowDefinitionResponse | undefined) {
	const [name, setName] = useState("");
	// Rows carry a client id for the whole editing session. The node KEY is a field being edited and an array index
	// moves when a row does, so neither can be a React key: one remounts the input on every keystroke and takes the
	// caret with it, the other hands a reordered row the state of the row it displaced.
	const [nodeRows, setNodeRows] = useState<readonly DraftRow<DevWorkflowGraphNode>[]>([]);
	const [edgeRows, setEdgeRows] = useState<readonly DraftRow<DevWorkflowGraphEdge>[]>([]);
	const [schemaVersion, setSchemaVersion] = useState(1);
	// The graph-level waiver of GRAPH-C4-2, held as a boolean and sent back as `true` or not at all: a template that
	// never waived anything must not GAIN an explicit `false`, which would rewrite a stored document to say something
	// it never said.
	const [allowUngatedWrites, setAllowUngatedWrites] = useState(false);
	const [saveError, setSaveError] = useState<string | undefined>(undefined);
	const [isConflict, setIsConflict] = useState(false);

	// Seeded when the definition lands and reseeded when its version moves — which is what a successful save does, and
	// what a reload after a 409 does. Keyed on identity rather than on the object so typing is never overwritten.
	const seedKey = `${definition?.id ?? ""}:${definition?.version ?? 0}`;
	// biome-ignore lint/correctness/useExhaustiveDependencies: seeding is keyed on identity, not on the document object.
	useEffect(() => {
		setName(definition?.name ?? "");
		setSchemaVersion(definition?.graph?.schemaVersion ?? 1);
		setAllowUngatedWrites(definition?.graph?.allowUngatedWrites === true);
		setNodeRows((definition?.graph?.nodes ?? []).map(toRow));
		setEdgeRows((definition?.graph?.edges ?? []).map(toRow));
		setSaveError(undefined);
		setIsConflict(false);
	}, [seedKey]);

	const nodes = nodeRows.map((row) => row.value);
	// Deduplicated: two nodes sharing a key is a state the editor must be able to RENDER (it is one of the issues it
	// reports), and Mantine refuses a Select whose options repeat a value.
	const nodeKeyOptions = [...new Set(nodes.map((node) => node.nodeKey ?? "").filter((key) => key.length > 0))];
	const graph: DevWorkflowGraph = useMemo(
		() => ({
			schemaVersion,
			nodes: nodeRows.map((row) => row.value),
			edges: edgeRows.map((row) => row.value),
			allowUngatedWrites: allowUngatedWrites ? true : undefined,
		}),
		[schemaVersion, nodeRows, edgeRows, allowUngatedWrites],
	);
	const issues = useMemo(() => validateDevWorkflowGraph(graph), [graph]);

	const patchNode = (id: string, patch: Partial<DevWorkflowGraphNode>): void =>
		setNodeRows((current) => current.map((row) => (row.id === id ? { ...row, value: { ...row.value, ...patch } } : row)));

	const moveNode = (id: string, offset: number): void =>
		setNodeRows((current) => {
			const index = current.findIndex((row) => row.id === id);
			const list = [...current];
			const moved = list[index];
			const displaced = list[index + offset];
			if (!moved || !displaced) {
				return current;
			}
			list[index] = displaced;
			list[index + offset] = moved;
			return list;
		});

	const patchEdge = (id: string, patch: Partial<DevWorkflowGraphEdge>): void =>
		setEdgeRows((current) => current.map((row) => (row.id === id ? { ...row, value: { ...row.value, ...patch } } : row)));

	return {
		name,
		setName,
		nodeRows,
		setNodeRows,
		edgeRows,
		setEdgeRows,
		allowUngatedWrites,
		setAllowUngatedWrites,
		saveError,
		setSaveError,
		isConflict,
		setIsConflict,
		nodeKeyOptions,
		graph,
		issues,
		patchNode,
		moveNode,
		patchEdge,
	};
}
