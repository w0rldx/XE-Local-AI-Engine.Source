import { Alert, Anchor, Badge, Button, Code, Collapse, Group, Paper, Skeleton, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import {
	asGraphWorkflowEventType,
	graphWorkflowEventTypeLabelKey,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { useGraphWorkflowRunEvents } from "@/features/graphWorkflows/queries/useGraphWorkflows";

export interface GraphWorkflowEventsTabProps {
	readonly runId: string;
}

/**
 * The run's trail. This is the one component in the feature that owns a query, exactly as `DevWorkflowEventsTab`'s
 * feed does: the events are nobody else's data, and threading them through the page would give the page a second
 * pagination state to keep.
 *
 * The feed is read forward from the start of the log (`afterSeq: 0`) and rendered newest-first, so `replayTruncated`
 * means the NEWEST events are not on screen yet — which is what the banner says, and what "Load more" fetches.
 */
export function GraphWorkflowEventsTab({ runId }: GraphWorkflowEventsTabProps) {
	const { t } = useTranslation();
	const [expandedId, setExpandedId] = useState<string | undefined>(undefined);
	const query = useGraphWorkflowRunEvents(runId);

	if (query.isPending) {
		return (
			<Stack gap="xs" data-testid="graph-workflow-events-loading">
				<Skeleton height={48} radius="md" />
				<Skeleton height={48} radius="md" />
			</Stack>
		);
	}

	if (query.isError) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="graph-workflow-events-error">
				<Stack gap="sm" align="flex-start">
					<Text size="sm">
						{apiErrorMessage(query.error, t("pages.graphWorkflows.events.loadFailed", "Could not load this run's events."))}
					</Text>
					<Button
						size="xs"
						variant="light"
						onClick={() => {
							query.refetch().catch(() => undefined);
						}}
						data-testid="graph-workflow-events-retry"
					>
						{t("pages.graphWorkflows.events.retry", "Retry")}
					</Button>
				</Stack>
			</Alert>
		);
	}

	// The feed arrives ascending and deduplicated on the sequence; the operator reads a run from its latest event back.
	const events = [...query.data.events].reverse();

	return (
		<Stack gap="xs" data-testid="graph-workflow-events-tab">
			{/* Not a silently short list: a trail cut at the cap looks exactly like a run that has done little. */}
			{query.data.replayTruncated ? (
				<Alert color="blue" variant="light" data-testid="graph-workflow-events-truncated">
					{t(
						"pages.graphWorkflows.events.truncated",
						"This run has more events than one page holds; these start at the beginning of the log. Load more to reach the latest.",
					)}
				</Alert>
			) : null}
			{events.length === 0 ? (
				<EmptyState
					message={t("pages.graphWorkflows.events.empty", "Nothing has happened in this run yet.")}
					data-testid="graph-workflow-events-empty"
				/>
			) : null}
			{events.map((event) => {
				const eventId = event.id ?? String(event.seq ?? 0);
				// An unknown token is shown raw rather than mislabelled as a neighbouring event.
				const known = asGraphWorkflowEventType(event.eventType);
				const label = known ? t(graphWorkflowEventTypeLabelKey(known), known) : (event.eventType ?? "");
				return (
					<Paper key={eventId} withBorder={true} p="xs" data-testid={`graph-workflow-event-${eventId}`}>
						<Stack gap={4}>
							<Group gap="xs" wrap="nowrap">
								<Badge size="xs" variant="light" data-testid={`graph-workflow-event-type-${eventId}`}>
									{label}
								</Badge>
								<Text size="xs" c="dimmed" style={{ flex: 1, minWidth: 0 }}>
									{t("pages.graphWorkflows.events.meta", "#{{seq}}", { seq: event.seq ?? 0 })}
								</Text>
								<Text size="xs" c="dimmed">
									{new Date(event.createdAtUtc ?? 0).toLocaleTimeString()}
								</Text>
							</Group>
							{/* Which node it happened on: the same key the canvas card and the table row are addressed by. */}
							{event.nodeKey ? (
								<Text size="xs" data-testid={`graph-workflow-event-node-${eventId}`}>
									{event.nodeKey}
								</Text>
							) : null}
							{event.detail != null ? (
								<>
									<Anchor
										component="button"
										type="button"
										size="xs"
										onClick={() => setExpandedId((current) => (current === eventId ? undefined : eventId))}
										data-testid={`graph-workflow-event-toggle-${eventId}`}
									>
										{expandedId === eventId
											? t("pages.graphWorkflows.events.hideDetail", "Hide detail")
											: t("pages.graphWorkflows.events.showDetail", "Show detail")}
									</Anchor>
									<Collapse expanded={expandedId === eventId}>
										<Code block={true} data-testid={`graph-workflow-event-detail-${eventId}`}>
											{JSON.stringify(event.detail, null, 2)}
										</Code>
									</Collapse>
								</>
							) : null}
						</Stack>
					</Paper>
				);
			})}
			{query.hasNextPage ? (
				<Button
					size="xs"
					variant="light"
					loading={query.isFetchingNextPage}
					onClick={() => {
						query.fetchNextPage().catch(() => undefined);
					}}
					data-testid="graph-workflow-events-load-more"
				>
					{t("pages.graphWorkflows.events.loadMore", "Load more")}
				</Button>
			) : null}
		</Stack>
	);
}
