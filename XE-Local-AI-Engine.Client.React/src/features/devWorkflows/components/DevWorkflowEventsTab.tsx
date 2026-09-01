import { Anchor, Badge, Button, Code, Collapse, Group, Paper, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { DevWorkflowRunEventResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import type { DevWorkflowEventsAnchor } from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowEventsTabProps {
	readonly events: readonly DevWorkflowRunEventResponse[];
	/** `nodeRunId` → node label. The event row carries no node key (P1 has no such column), so it is joined here. */
	readonly labelByNodeRunId: ReadonlyMap<string, string>;
	/** Whether the run has events past the pages already loaded. Every one of them is reachable — the feed is cursor-paged. */
	readonly hasMore: boolean;
	readonly isLoadingMore: boolean;
	/** Which end the feed is anchored on (R-C4). Decides which direction "load more" walks, and what it is called. */
	readonly anchor: DevWorkflowEventsAnchor;
	readonly onAnchorChange: (anchor: DevWorkflowEventsAnchor) => void;
	readonly onLoadMore: () => void;
	readonly onSelectNode: (nodeRunId: string) => void;
}

export function DevWorkflowEventsTab({
	events,
	labelByNodeRunId,
	hasMore,
	isLoadingMore,
	anchor,
	onAnchorChange,
	onLoadMore,
	onSelectNode,
}: DevWorkflowEventsTabProps) {
	const { t } = useTranslation();
	const [expandedId, setExpandedId] = useState<string | undefined>(undefined);

	// Sequences are strictly increasing but NOT contiguous — the run's counter is shared with node-runs and artifacts,
	// so a gap between two event numbers is normal and must never be read as a lost event.
	const ordered = events.toSorted((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0));

	return (
		<Stack gap="xs" data-testid="dev-workflow-events-tab">
			{/* Which end of the log is on screen, and the way to the other one. The feed opens on the newest events, so
			    "jump to the start" is the affordance that is NOT the default and has to be offered explicitly.

			    Rendered ABOVE the empty check, never inside it. The anchored window is a range of SEQUENCE numbers and
			    the run's counter is shared with node-runs and artifacts, so a tail window can legitimately hold no
			    events at all on a wide fan-out — and an empty state that also swallowed these controls would strand the
			    operator on a blank page, one click from a log full of rows they could no longer reach. */}
			<Group gap="xs" wrap="wrap">
				<Button
					size="compact-xs"
					variant={anchor === "newest" ? "light" : "subtle"}
					onClick={() => onAnchorChange("newest")}
					data-testid="dev-workflow-events-jump-newest"
				>
					{t("pages.devWorkflows.events.jumpNewest", "Newest")}
				</Button>
				<Button
					size="compact-xs"
					variant={anchor === "oldest" ? "light" : "subtle"}
					onClick={() => onAnchorChange("oldest")}
					data-testid="dev-workflow-events-jump-oldest"
				>
					{t("pages.devWorkflows.events.jumpOldest", "Oldest")}
				</Button>
			</Group>
			{ordered.length === 0 ? (
				<EmptyState
					message={
						anchor === "newest"
							? t(
									"pages.devWorkflows.events.emptyWindow",
									"No events in the most recent part of this run's log. Older events, if there are any, are one step back.",
								)
							: t("pages.devWorkflows.events.empty", "Nothing has happened in this run yet.")
					}
					data-testid="dev-workflow-events-empty"
				/>
			) : null}
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
				<Button
					size="xs"
					variant="light"
					loading={isLoadingMore}
					onClick={onLoadMore}
					data-testid="dev-workflow-events-load-more"
				>
					{anchor === "newest"
						? t("pages.devWorkflows.events.loadOlder", "Load older")
						: t("pages.devWorkflows.events.loadMore", "Load more")}
				</Button>
			) : null}
		</Stack>
	);
}
