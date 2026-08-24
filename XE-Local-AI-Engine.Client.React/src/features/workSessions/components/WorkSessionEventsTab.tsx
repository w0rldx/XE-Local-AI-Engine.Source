import { Alert, Anchor, Badge, Button, Code, Collapse, Group, Paper, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { WorkSessionEventResponse } from "@/features/workSessions/models/WorkSessionModels";

export interface WorkSessionEventsTabProps {
	readonly events: readonly WorkSessionEventResponse[];
	/** The server has more events beyond the current page. */
	readonly hasMore: boolean;
	/** False once the page size has reached the server's clamp — asking for more would silently return the same page. */
	readonly canLoadMore: boolean;
	readonly onLoadMore: () => void;
}

export function WorkSessionEventsTab({ events, hasMore, canLoadMore, onLoadMore }: WorkSessionEventsTabProps) {
	const { t } = useTranslation();
	const [expandedId, setExpandedId] = useState<string | undefined>(undefined);

	if (events.length === 0) {
		return (
			<Alert color="gray" variant="light" data-testid="work-session-events-empty">
				{t("pages.workSessions.events.empty", "Nothing has happened in this session yet.")}
			</Alert>
		);
	}

	const ordered = events.toSorted((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0));

	return (
		<Stack gap="xs" data-testid="work-session-events-tab">
			{ordered.map((event) => (
				<Paper key={event.id} withBorder={true} p="xs" data-testid={`work-session-event-${event.id}`}>
					<Stack gap={4}>
						<Group gap="xs" wrap="nowrap">
							<Badge size="xs" variant="light">
								{event.eventType}
							</Badge>
							<Text size="xs" c="dimmed" style={{ flex: 1, minWidth: 0 }}>
								{t("pages.workSessions.events.meta", "step {{step}} · #{{sequence}}", {
									step: event.step ?? 0,
									sequence: event.sequence ?? 0,
								})}
							</Text>
							<Text size="xs" c="dimmed">
								{new Date(event.occurredAtUtc ?? 0).toLocaleTimeString()}
							</Text>
						</Group>
						{event.outcome ? (
							<Text size="xs" data-testid={`work-session-event-outcome-${event.id}`}>
								{event.outcome}
							</Text>
						) : null}
						{event.detailJson ? (
							<>
								<Anchor
									component="button"
									type="button"
									size="xs"
									onClick={() => setExpandedId((current) => (current === event.id ? undefined : event.id))}
									data-testid={`work-session-event-toggle-${event.id}`}
								>
									{expandedId === event.id
										? t("pages.workSessions.events.hideDetail", "Hide detail")
										: t("pages.workSessions.events.showDetail", "Show detail")}
								</Anchor>
								<Collapse expanded={expandedId === event.id}>
									<Code block={true} data-testid={`work-session-event-detail-${event.id}`}>
										{event.detailJson}
									</Code>
								</Collapse>
							</>
						) : null}
					</Stack>
				</Paper>
			))}
			{hasMore ? (
				canLoadMore ? (
					<Button size="xs" variant="light" onClick={onLoadMore} data-testid="work-session-events-load-more">
						{t("pages.workSessions.events.loadMore", "Load more")}
					</Button>
				) : (
					<Text size="xs" c="dimmed" data-testid="work-session-events-truncated">
						{t("pages.workSessions.events.truncated", "Only the first events are shown.")}
					</Text>
				)
			) : null}
		</Stack>
	);
}
