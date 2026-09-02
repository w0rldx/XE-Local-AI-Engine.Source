import { Button, Code, Group, ScrollArea, Stack, Text } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevelopmentEvent } from "@/features/development/models/DevelopmentModels";

/**
 * The selected task's durable events — and, kept apart, any the store bound to no task at all.
 *
 * The two are not one list: a row with no task is not this task's evidence, and putting it under the selected task is
 * the same misattribution as showing a sibling's. `untiedEvents` is empty for every event kind the store writes today,
 * so the second group renders nothing at all until one exists.
 */
export function DevelopmentEventTimeline({
	events,
	untiedEvents,
	onRefresh,
}: {
	readonly events: readonly DevelopmentEvent[];
	readonly untiedEvents: readonly DevelopmentEvent[];
	readonly onRefresh: () => void;
}) {
	const { t } = useTranslation();
	return (
		<SectionCard
			actions={
				<Button leftSection={<IconRefresh size={14} />} onClick={onRefresh} size="xs" variant="subtle">
					{t("common.refresh", "Refresh")}
				</Button>
			}
			title={t("pages.development.timeline.title", "Durable event timeline")}
		>
			<ScrollArea h={260}>
				<Stack gap="xs">
					{events.map((event) => (
						<EventRow event={event} key={event.id} />
					))}
					{untiedEvents.length > 0 ? (
						<>
							<Text c="dimmed" fw={500} size="xs" mt="xs">
								{t("pages.development.timeline.projectScope", "Not tied to a task")}
							</Text>
							{untiedEvents.map((event) => (
								<EventRow event={event} key={event.id} />
							))}
						</>
					) : null}
				</Stack>
			</ScrollArea>
		</SectionCard>
	);
}

function EventRow({ event }: { readonly event: DevelopmentEvent }) {
	const { t } = useTranslation();
	return (
		<Stack gap={2} data-testid="development-event-row">
			<Group justify="space-between" wrap="nowrap">
				<Text size="sm">
					<Code>#{event.sequence}</Code> {event.eventType}
				</Text>
				<Text c="dimmed" size="xs">
					{event.outcome ?? event.operationPhase ?? ""}
				</Text>
			</Group>
			{/* Why a task was sent back, in the words of whatever sent it — a workflow's validation report or a
			    reviewer. Rendered as PLAIN TEXT with its own line breaks kept: it is model-written prose the server
			    sanitized and capped, and parsing it as markdown would hand a generated document a say in the layout of
			    the page an operator reads to find out what went wrong. */}
			{event.reason ? (
				<Text c="dimmed" size="xs" style={{ whiteSpace: "pre-wrap" }} data-testid={`development-event-reason-${event.id}`}>
					{/* The label is translated; the sentence is data and is never interpolated into a phrase. */}
					{t("pages.development.timeline.reason", "Sent back:")} {event.reason}
				</Text>
			) : null}
		</Stack>
	);
}
