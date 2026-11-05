// The dirty check behind the Save button: order-independent structural equality of two wire graphs. Pure: no React.

import { graphSettingsFromWire } from "@/features/graphWorkflows/models/GraphWorkflowCanvasConversion";
import {
	configRecord,
	type GraphWorkflowGraph,
	narrowGraphWorkflowJoinPolicy,
	normalizeGraphWorkflowConditionOperator,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/** Recursive key sort, so two documents that differ only in member order compare equal. Nulls are preserved. */
function canonicalize(value: unknown): unknown {
	if (Array.isArray(value)) {
		return value.map((entry) => canonicalize(entry));
	}
	if (typeof value === "object" && value !== null) {
		return Object.fromEntries(
			Object.entries(value as Record<string, unknown>)
				.toSorted(([left], [right]) => left.localeCompare(right))
				.map(([key, entry]) => [key, canonicalize(entry)]),
		);
	}
	return value;
}

/** The config with its own `null`/`undefined` members dropped, so `{ resultPath: null }` and `{}` are one graph. Only
 * the TOP level: a null inside the operator's own JSON document is data, and dropping it would hide a real edit. */
function normalizedConfig(config: unknown): unknown {
	return canonicalize(
		Object.fromEntries(Object.entries(configRecord(config)).filter(([, value]) => value !== null && value !== undefined)),
	);
}

function normalizedGraph(graph: GraphWorkflowGraph | undefined, ignorePositions = false): string {
	const nodes = (graph?.nodes ?? [])
		.map((node) => ({
			key: node.key ?? "",
			kind: node.kind ?? "",
			label: node.label ?? "",
			// A node with NO position is not the same graph as one placed at the origin: the layout makes it dirty, and
			// the first save is what persists the positions it computed.
			position: ignorePositions
				? null
				: node.position
					? { x: Math.round(node.position.x ?? 0), y: Math.round(node.position.y ?? 0) }
					: null,
			maxAttempts: node.maxAttempts ?? null,
			timeoutSeconds: node.timeoutSeconds ?? null,
			joinPolicy: narrowGraphWorkflowJoinPolicy(node.joinPolicy),
			config: normalizedConfig(node.config),
		}))
		.toSorted((left, right) => left.key.localeCompare(right.key));
	const edges = (graph?.edges ?? [])
		.map((edge) => ({
			key: edge.key ?? "",
			from: edge.from ?? "",
			to: edge.to ?? "",
			label: edge.label ?? "",
			sourceHandle: edge.sourceHandle ?? null,
			condition: edge.condition
				? {
						path: edge.condition.path ?? null,
						// Normalized, so a stored lowercase `eq` and the canonical `Eq` are the same branch.
						op: normalizeGraphWorkflowConditionOperator(edge.condition.op) ?? edge.condition.op ?? "",
						value: canonicalize(edge.condition.value ?? null),
					}
				: null,
		}))
		.toSorted((left, right) => left.key.localeCompare(right.key));
	// Absent `kind` and `Standard` are one graph; the `chat` block compares member by member as stored.
	const settings = graphSettingsFromWire(graph);
	return JSON.stringify({
		schemaVersion: graph?.schemaVersion ?? 1,
		kind: settings.kind,
		chat: settings.chat === undefined ? null : canonicalize(settings.chat),
		nodes,
		edges,
	});
}

/**
 * Order-independent structural equality, INCLUDING positions and edge label/condition/sourceHandle — a moved node is a
 * real edit here, unlike Preview, because this wire persists `position`. Absent ≡ null ≡ the parser's default
 * (`joinPolicy` `All`, `schemaVersion` 1, `label` ""). Drives the Save button and `useUnsavedChangesGuard`.
 *
 * A graph the EDITOR saved always reads clean on reopen. A terse hand-authored one (no positions, a Pause with no
 * `requireComment`, an End with no `outcome`) reads as dirty the moment it is opened, because opening it fills those in
 * — which is the truth: saving would rewrite the document. That is a Save button that is enabled, not a false diff.
 */
export function graphWorkflowsEqual(a: GraphWorkflowGraph | undefined, b: GraphWorkflowGraph | undefined): boolean {
	return normalizedGraph(a) === normalizedGraph(b);
}

/** {@link graphWorkflowsEqual} with every node position ignored: the two graphs differ at most in their layout. */
export function graphWorkflowsEqualIgnoringPositions(
	a: GraphWorkflowGraph | undefined,
	b: GraphWorkflowGraph | undefined,
): boolean {
	return normalizedGraph(a, true) === normalizedGraph(b, true);
}
