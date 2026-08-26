import { Button, Code, Group, ScrollArea, Stack, Text } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevelopmentEvent } from "@/features/development/models/DevelopmentModels";

export function DevelopmentEventTimeline({
	events,
	onRefresh,
}: {
	readonly events: readonly DevelopmentEvent[];
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
						<Group justify="space-between" key={event.id} wrap="nowrap">
							<Text size="sm">
								<Code>#{event.sequence}</Code> {event.eventType}
							</Text>
							<Text c="dimmed" size="xs">
								{event.outcome ?? event.operationPhase ?? ""}
							</Text>
						</Group>
					))}
				</Stack>
			</ScrollArea>
		</SectionCard>
	);
}
