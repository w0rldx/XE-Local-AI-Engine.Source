import { Anchor, Badge, Button, Code, Collapse, Group, Paper, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { DevWorkflowRunEventResponse } from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowEventsTabProps {
	readonly events: readonly DevWorkflowRunEventResponse[];
	/** `nodeRunId` → node label. The event row carries no node key (P1 has no such column), so it is joined here. */
	readonly labelByNodeRunId: ReadonlyMap<string, string>;
	readonly hasMore: boolean;
	/** False once the page size has reached the server's clamp — asking for more would return the same page. */
	readonly canLoadMore: boolean;
	readonly onLoadMore: () => void;
	readonly onSelectNode: (nodeRunId: string) => void;
}

export function DevWorkflowEventsTab({
	events,
	labelByNodeRunId,
	hasMore,
	canLoadMore,
	onLoadMore,
	onSelectNode,
}: DevWorkflowEventsTabProps) {
	const { t } = useTranslation();
	const [expandedId, setExpandedId] = useState<string | undefined>(undefined);

	if (events.length === 0) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.events.empty", "Nothing has happened in this run yet.")}
				data-testid="dev-workflow-events-empty"
			/>
		);
	}

	// Sequences are strictly increasing but NOT contiguous — the run's counter is shared with node-runs and artifacts,
	// so a gap between two event numbers is normal and must never be read as a lost event.
	const ordered = events.toSorted((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0));

	return (
		<Stack gap="xs" data-testid="dev-workflow-events-tab">
			{ordered.map((event) => {
				const nodeLabel = event.nodeRunId ? labelByNodeRunId.get(event.nodeRunId) : undefined;
				return (
					<Paper key={event.id} withBorder={true} p="xs" data-testid={`dev-workflow-event-${event.id}`}>
						<Stack gap={4}>
							<Group gap="xs" wrap="nowrap">
								<Badge size="xs" variant="light">
									{event.eventType}
								</Badge>
								<Text size="xs" c="dimmed" style={{ flex: 1, minWidth: 0 }}>
									{t("pages.devWorkflows.events.meta", "#{{sequence}}", { sequence: event.sequence ?? 0 })}
								</Text>
								<Text size="xs" c="dimmed">
									{new Date(event.occurredAtUtc ?? 0).toLocaleTimeString()}
								</Text>
							</Group>
							{nodeLabel || event.outcome ? (
								<Group gap="xs" wrap="wrap">
									{nodeLabel && event.nodeRunId ? (
										<Anchor
											component="button"
											type="button"
											size="xs"
											onClick={() => onSelectNode(event.nodeRunId ?? "")}
											data-testid={`dev-workflow-event-node-${event.id}`}
										>
											{nodeLabel}
										</Anchor>
									) : null}
									{event.outcome ? (
										<Text size="xs" data-testid={`dev-workflow-event-outcome-${event.id}`}>
											{event.outcome}
										</Text>
									) : null}
								</Group>
							) : null}
							{event.detailJson ? (
								<>
									<Anchor
										component="button"
										type="button"
										size="xs"
										onClick={() => setExpandedId((current) => (current === event.id ? undefined : event.id))}
										data-testid={`dev-workflow-event-toggle-${event.id}`}
									>
										{expandedId === event.id
											? t("pages.devWorkflows.events.hideDetail", "Hide detail")
											: t("pages.devWorkflows.events.showDetail", "Show detail")}
									</Anchor>
									<Collapse expanded={expandedId === event.id}>
										<Code block={true} data-testid={`dev-workflow-event-detail-${event.id}`}>
											{event.detailJson}
										</Code>
									</Collapse>
								</>
							) : null}
						</Stack>
					</Paper>
				);
			})}
			{hasMore ? (
				canLoadMore ? (
					<Button size="xs" variant="light" onClick={onLoadMore} data-testid="dev-workflow-events-load-more">
						{t("pages.devWorkflows.events.loadMore", "Load more")}
					</Button>
				) : (
					<Text size="xs" c="dimmed" data-testid="dev-workflow-events-truncated">
						{t("pages.devWorkflows.events.truncated", "Only the first events are shown.")}
					</Text>
				)
			) : null}
		</Stack>
	);
}
