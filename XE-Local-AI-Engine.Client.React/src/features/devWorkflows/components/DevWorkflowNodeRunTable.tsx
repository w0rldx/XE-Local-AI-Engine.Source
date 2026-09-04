import { Anchor, Badge, Group, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { DevWorkflowNodeStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import {
	type DevWorkflowNodeRunSummaryResponse,
	type DevWorkflowNodeStatus,
	devWorkflowAttemptCounts,
	devWorkflowAttemptLabel,
	formatDevWorkflowDuration,
	toDevWorkflowNodeStatus,
	toDevWorkflowNodeType,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowNodeRunTableProps {
	readonly nodes: readonly DevWorkflowNodeRunSummaryResponse[];
	readonly selectedNodeRunId?: string;
	readonly onSelect: (nodeRunId: string) => void;
}

/**
 * The second line of a row: what this node is actually doing, in the words its state earns.
 *
 * This function is the frontend half of O9. `Queued` says which slot it is waiting for and for how long; `Running`
 * says how long it has been running; `Pending` names the nodes it is waiting on. None of the three may borrow another
 * one's copy, because "in progress" over a queued node claims a GPU is working on it when the GPU is serving someone
 * else.
 */
function statusLine(
	node: DevWorkflowNodeRunSummaryResponse,
	status: DevWorkflowNodeStatus,
	labelByNodeKey: ReadonlyMap<string, string>,
	t: TFunction,
): string {
	const now = Date.now();
	switch (status) {
		case "Pending": {
			const waitingOn = node.waitingOnNodeKeys ?? [];
			if (waitingOn.length === 0) {
				return t("pages.devWorkflows.nodes.notReached", "not reached yet");
			}
			return t("pages.devWorkflows.nodes.waitingOn", "waiting on {{nodes}}", {
				nodes: waitingOn.map((key) => labelByNodeKey.get(key) ?? key).join(", "),
			});
		}
		case "Queued": {
			// A closed token list (P2 §7.1), so it gets translated labels like any other enum — with an explicit generic
			// fallback, because it is not narrowed and a newer server may add one.
			const reason = node.queueReason
				? t(`pages.devWorkflows.queueReason.${node.queueReason}`, t("pages.devWorkflows.queueReason.unknown", "queued"))
				: t("pages.devWorkflows.queueReason.unknown", "queued");
			return node.queuedAtUtc
				? t("pages.devWorkflows.nodes.queuedFor", "{{reason}} · queued for {{duration}}", {
						reason,
						duration: formatDevWorkflowDuration(now - node.queuedAtUtc),
					})
				: reason;
		}
		case "Running":
			return node.startedAtUtc
				? t("pages.devWorkflows.nodes.runningFor", "running for {{duration}}", {
						duration: formatDevWorkflowDuration(now - node.startedAtUtc),
					})
				: t("pages.devWorkflows.nodes.running", "running");
		case "WaitingForApproval":
			return t("pages.devWorkflows.nodes.needsDecision", "needs your decision");
		case "Blocked":
			// Y20: this is not a dependency wait. The run has stopped and only Retry / Skip / Abandon restarts it.
			return t("pages.devWorkflows.nodes.needsIntervention", "needs your intervention");
		// `Succeeded`, `Failed`, `Skipped`, `Cancelled` — every state where the node has stopped and its duration is the
		// only thing left to say. The wire value is already narrowed to the nine, so nothing else reaches this arm.
		default:
			return node.startedAtUtc && node.completedAtUtc
				? t("pages.devWorkflows.nodes.took", "took {{duration}}", {
						duration: formatDevWorkflowDuration(node.completedAtUtc - node.startedAtUtc),
					})
				: t("pages.devWorkflows.nodes.finished", "finished");
	}
}

/**
 * What the node run's LAST attempt cost, in the two numbers a row has space for. A dash is "nothing was reported" —
 * a structural node, a row from before this was collected, or a collection that could not run — and never zero,
 * which would claim the attempt was free. Earlier attempts are not here: the row keeps the last one only.
 */
function costLine(node: DevWorkflowNodeRunSummaryResponse, t: TFunction): string {
	const parts: string[] = [];
	if (node.inputTokens != null || node.outputTokens != null) {
		parts.push(
			t("pages.devWorkflows.nodes.costTokens", "{{input}} / {{output}} tok", {
				input: node.inputTokens?.toLocaleString() ?? "–",
				output: node.outputTokens?.toLocaleString() ?? "–",
			}),
		);
	}
	if (node.toolCalls != null) {
		parts.push(t("pages.devWorkflows.nodes.costToolCalls", "{{count}} tool calls", { count: node.toolCalls }));
	}
	return parts.length === 0 ? "—" : parts.join(" · ");
}

/**
 * THE execution view in Slice A0 (Y8): one row per node-run, in the order the runtime allocated them. No canvas, no
 * layout engine — and this stays the accessible, small-screen path to every node once the graph arrives in A1.
 */
export function DevWorkflowNodeRunTable({ nodes, selectedNodeRunId, onSelect }: DevWorkflowNodeRunTableProps) {
	const { t } = useTranslation();

	if (nodes.length === 0) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.nodes.empty", "This run has no node-runs yet.")}
				data-testid="dev-workflow-node-runs-empty"
			/>
		);
	}

	const ordered = nodes.toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0));
	const labelByNodeKey = new Map(nodes.map((node) => [node.nodeKey ?? "", node.label ?? node.nodeKey ?? ""]));

	return (
		<Table.ScrollContainer minWidth={520} data-testid="dev-workflow-node-run-table">
			<Table highlightOnHover={true} verticalSpacing="xs">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.devWorkflows.nodes.columnNode", "Node")}</Table.Th>
						<Table.Th>{t("pages.devWorkflows.nodes.columnStatus", "Status")}</Table.Th>
						<Table.Th>{t("pages.devWorkflows.nodes.columnDetail", "Detail")}</Table.Th>
						<Table.Th>{t("pages.devWorkflows.nodes.columnCost", "Cost")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{ordered.map((node) => {
						const status = toDevWorkflowNodeStatus(node.status);
						const nodeType = toDevWorkflowNodeType(node.nodeType);
						const counts = devWorkflowAttemptCounts(node.attempt, node.maxAttempts, node.operatorRetries);
						return (
							<Table.Tr
								key={node.id}
								onClick={() => onSelect(node.id ?? "")}
								bg={node.id === selectedNodeRunId ? "var(--mantine-color-default-hover)" : undefined}
								style={{ cursor: "pointer" }}
								data-testid={`dev-workflow-node-row-${node.id}`}
							>
								<Table.Td>
									<Stack gap={2} align="flex-start">
										{/* The row's onClick is the pointer affordance; THIS is the control a keyboard and a
										    screen reader can reach, and this table is A0's only execution view. A click here
										    also bubbles to the row and selects the same id, so the second call is a no-op. */}
										<Anchor
											component="button"
											type="button"
											ta="left"
											onClick={() => onSelect(node.id ?? "")}
											data-testid={`dev-workflow-node-select-${node.id}`}
										>
											<Text size="sm" fw={500} lineClamp={1}>
												{node.label}
											</Text>
										</Anchor>
										<Group gap={4} wrap="wrap">
											<Badge size="xs" variant="light" color="gray">
												{t(`pages.devWorkflows.nodeType.${nodeType}`, nodeType)}
											</Badge>
											{node.isMaterialized ? (
												<Badge size="xs" variant="outline" color="gray">
													{t("pages.devWorkflows.nodes.materialized", "generated")}
												</Badge>
											) : null}
											{/* D12: the row is a real `Succeeded` check — it has to be, or the join behind it would
											    never let the apply through — but it stands for work that did not happen. Said HERE
											    and not only in the drill-down, because this table is where an operator reads a run. */}
											{node.validationNotApplicable ? (
												<Badge
													size="xs"
													variant="outline"
													color="gray"
													data-testid={`dev-workflow-node-not-applicable-${node.id}`}
												>
													{t("pages.devWorkflows.nodes.notApplicable", "nothing to validate")}
												</Badge>
											) : null}
											{node.hasStaleInputs ? (
												<Badge size="xs" variant="light" color="orange" data-testid={`dev-workflow-node-stale-${node.id}`}>
													{t("pages.devWorkflows.nodes.staleInputs", "Stale inputs")}
												</Badge>
											) : null}
										</Group>
									</Stack>
								</Table.Td>
								<Table.Td>
									<Stack gap={2}>
										<DevWorkflowNodeStatusBadge status={status} testId={`dev-workflow-node-status-${node.id}`} />
										{counts.maxAttempts > 1 && counts.attempt > 1 ? (
											<Text size="xs" c="dimmed" data-testid={`dev-workflow-node-attempt-${node.id}`}>
												{devWorkflowAttemptLabel(t, counts)}
											</Text>
										) : null}
									</Stack>
								</Table.Td>
								<Table.Td>
									<Stack gap={2}>
										<Group gap={6} wrap="nowrap">
											{status === "Blocked" ? <IconAlertTriangle size={14} color="var(--mantine-color-red-6)" /> : null}
											<Text size="xs" data-testid={`dev-workflow-node-detail-${node.id}`}>
												{statusLine(node, status, labelByNodeKey, t)}
											</Text>
										</Group>
										{node.agentDisplayName ? (
											<Text size="xs" c="dimmed" lineClamp={1}>
												{node.modelLabel
													? t("pages.devWorkflows.nodes.agentWithModel", "{{agent}} · {{model}}", {
															agent: node.agentDisplayName,
															model: node.modelLabel,
														})
													: node.agentDisplayName}
											</Text>
										) : null}
									</Stack>
								</Table.Td>
								<Table.Td>
									<Text size="xs" c="dimmed" data-testid={`dev-workflow-node-cost-${node.id}`}>
										{costLine(node, t)}
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
