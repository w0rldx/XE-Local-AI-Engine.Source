import { Anchor, Badge, Group, Stack, Table, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { GraphWorkflowNodeStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	asGraphWorkflowDecisionKind,
	type GraphWorkflowNodeRunSummaryResponse,
	narrowGraphWorkflowFailureClass,
	narrowGraphWorkflowNodeKind,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/** Same scale as `formatDevWorkflowDuration`: seconds under a minute, then m/s, then h/m. Module-local by design. */
function formatDuration(milliseconds: number): string {
	const totalSeconds = Math.max(0, Math.round(milliseconds / 1000));
	if (totalSeconds < 60) {
		return `${totalSeconds}s`;
	}
	const minutes = Math.floor(totalSeconds / 60);
	if (minutes < 60) {
		return `${minutes}m ${String(totalSeconds % 60).padStart(2, "0")}s`;
	}
	return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, "0")}m`;
}

export interface GraphWorkflowNodeRunTableProps {
	readonly nodeRuns: readonly GraphWorkflowNodeRunSummaryResponse[];
	readonly selectedNodeKey?: string;
	readonly onSelectNode: (nodeKey: string) => void;
}

/**
 * THE authoritative path through a run: one row per node run, and the only one a keyboard or a screen reader can walk.
 * The canvas is the second view over the same selection.
 *
 * A node run carries no label on the wire — the server materializes rows keyed by node key — so the key IS the name
 * here, and the definition's label lives on the card the canvas draws.
 */
export function GraphWorkflowNodeRunTable({ nodeRuns, selectedNodeKey, onSelectNode }: GraphWorkflowNodeRunTableProps) {
	// The UI language, not the browser's: a German UI must not print US dates.
	const { t, i18n } = useTranslation();

	if (nodeRuns.length === 0) {
		return (
			<EmptyState
				message={t("pages.graphWorkflows.nodeTable.empty", "This run has no nodes yet.")}
				data-testid="graph-workflow-node-runs-empty"
			/>
		);
	}

	// Start order is the order the run happened in. A node that never started sorts LAST rather than first: it has not
	// happened yet, and a `Pending` row at the top of an execution log reads as the run's first step.
	const ordered = nodeRuns.toSorted((left, right) => {
		const byStart = (left.startedAtUtc ?? Number.MAX_SAFE_INTEGER) - (right.startedAtUtc ?? Number.MAX_SAFE_INTEGER);
		return byStart !== 0 ? byStart : (left.nodeKey ?? "").localeCompare(right.nodeKey ?? "");
	});

	return (
		<Table.ScrollContainer minWidth={560} data-testid="graph-workflow-node-run-table">
			<Table highlightOnHover={true} verticalSpacing="xs">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.graphWorkflows.nodeTable.columnNode", "Node")}</Table.Th>
						<Table.Th>{t("pages.graphWorkflows.nodeTable.columnKind", "Kind")}</Table.Th>
						<Table.Th>{t("pages.graphWorkflows.nodeTable.columnStatus", "Status")}</Table.Th>
						<Table.Th>{t("pages.graphWorkflows.nodeTable.columnAttempt", "Attempt")}</Table.Th>
						<Table.Th>{t("pages.graphWorkflows.nodeTable.columnStarted", "Started")}</Table.Th>
						<Table.Th>{t("pages.graphWorkflows.nodeTable.columnDuration", "Duration")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{ordered.map((nodeRun) => {
						const nodeKey = nodeRun.nodeKey ?? "";
						const kind = narrowGraphWorkflowNodeKind(nodeRun.kind);
						const failureClass = narrowGraphWorkflowFailureClass(nodeRun.failureClass);
						const pending = asGraphWorkflowDecisionKind(nodeRun.pendingDecisionKind);
						const duration =
							nodeRun.startedAtUtc != null && nodeRun.completedAtUtc != null
								? formatDuration(nodeRun.completedAtUtc - nodeRun.startedAtUtc)
								: "—";
						return (
							<Table.Tr
								key={nodeRun.id ?? nodeKey}
								onClick={() => onSelectNode(nodeKey)}
								bg={nodeKey === selectedNodeKey ? "var(--mantine-color-default-hover)" : undefined}
								style={{ cursor: "pointer" }}
								data-testid={`graph-workflow-node-row-${nodeKey}`}
							>
								<Table.Td>
									{/* The row's onClick is the pointer affordance; THIS is the control a keyboard and a screen
									    reader reach. A click here also bubbles to the row and selects the same key, so the
									    second call is a no-op. */}
									<Anchor
										component="button"
										type="button"
										ta="left"
										onClick={() => onSelectNode(nodeKey)}
										data-testid={`graph-workflow-node-select-${nodeKey}`}
									>
										<Text size="sm" fw={500} lineClamp={1}>
											{nodeKey}
										</Text>
									</Anchor>
								</Table.Td>
								<Table.Td>
									<Badge size="xs" variant="light" color="gray">
										{t(`pages.graphWorkflows.nodeKind.${kind}`, kind)}
									</Badge>
								</Table.Td>
								<Table.Td>
									<Stack gap={2} align="flex-start">
										<GraphWorkflowNodeStatusBadge
											status={nodeRun.status}
											data-testid={`graph-workflow-node-status-${nodeKey}`}
										/>
										<Group gap={4} wrap="wrap">
											{/* `None` is "nothing went wrong" — rendering it would put a failure word on every row. */}
											{failureClass !== "None" ? (
												<Badge size="xs" variant="light" color="red" data-testid={`graph-workflow-node-failure-${nodeKey}`}>
													{t(`pages.graphWorkflows.failureClass.${failureClass}`, failureClass)}
												</Badge>
											) : null}
											{pending ? (
												<Badge size="xs" variant="light" color="orange" data-testid={`graph-workflow-node-pending-${nodeKey}`}>
													{t("pages.graphWorkflows.nodeTable.decisionPending", "needs your decision")}
												</Badge>
											) : null}
										</Group>
									</Stack>
								</Table.Td>
								<Table.Td>
									<Text size="xs" data-testid={`graph-workflow-node-attempt-${nodeKey}`}>
										{nodeRun.attempt ?? 1}
									</Text>
								</Table.Td>
								<Table.Td>
									<Text size="xs" c="dimmed" data-testid={`graph-workflow-node-started-${nodeKey}`}>
										{nodeRun.startedAtUtc != null ? new Date(nodeRun.startedAtUtc).toLocaleTimeString(i18n.language) : "—"}
									</Text>
								</Table.Td>
								<Table.Td>
									<Text size="xs" c="dimmed" data-testid={`graph-workflow-node-duration-${nodeKey}`}>
										{duration}
									</Text>
								</Table.Td>
							</Table.Tr>
						);
					})}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
