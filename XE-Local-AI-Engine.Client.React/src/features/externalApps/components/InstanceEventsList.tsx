import { Button, Skeleton, Stack, Table } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { ExternalAppInstanceEventView } from "@/features/externalApps/models/ExternalAppModels";
import { toExternalAppEventKind } from "@/features/externalApps/models/ExternalAppModels";
import { useExternalAppInstanceEvents } from "@/features/externalApps/queries/useExternalApps";

interface InstanceEventsListProps {
	readonly instanceId: string;
	readonly "data-testid"?: string;
}

const keyPrefix = "pages.externalApps.events";

/**
 * The append-only history, ascending with the newest row at the bottom.
 *
 * The feed pages FORWARD: `afterSequence` is an exclusive LOWER bound, so "load more" advances it to the last
 * sequence returned and appends. Decreasing it would repeat the page just read.
 *
 * Events are content-free by design, so a row is a sequence, a time and a translated kind — and an unrecognised kind
 * renders `events.kind.unknown` rather than putting a raw identifier from a newer server in front of an operator.
 */
export function InstanceEventsList({ instanceId, "data-testid": testId }: InstanceEventsListProps) {
	const { t } = useTranslation();
	// Every page already read, kept here because the query is keyed on `afterSequence` and answers one page only.
	const [loaded, setLoaded] = useState<readonly ExternalAppInstanceEventView[]>([]);
	const [afterSequence, setAfterSequence] = useState(0);

	const eventsQuery = useExternalAppInstanceEvents(instanceId, { afterSequence });
	const page = eventsQuery.data?.items ?? [];
	const rows = afterSequence === 0 ? page : [...loaded, ...page];
	const hasMore = eventsQuery.data?.hasMore === true;

	const loadMore = (): void => {
		const lastSequence = page.at(-1)?.sequence;
		if (lastSequence === undefined) {
			return;
		}
		setLoaded(rows);
		setAfterSequence(lastSequence);
	};

	// "No history yet" is a claim about the node, and the first read has not answered yet — so it waits rather than
	// asserting an empty feed for the instant the query is in flight.
	if (eventsQuery.isLoading) {
		return <Skeleton height={120} radius="md" data-testid="external-app-events-loading" />;
	}

	if (rows.length === 0) {
		return <EmptyState message={t(`${keyPrefix}.empty`)} data-testid="external-app-events-empty" />;
	}

	return (
		<Stack gap="sm" data-testid={testId ?? "external-app-events"}>
			<Table highlightOnHover={true}>
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t(`${keyPrefix}.columns.sequence`)}</Table.Th>
						<Table.Th>{t(`${keyPrefix}.columns.time`)}</Table.Th>
						<Table.Th>{t(`${keyPrefix}.columns.kind`)}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{rows.map((event) => {
						const kind = toExternalAppEventKind(event.kind);
						return (
							<Table.Tr key={event.sequence} data-testid={`external-app-event-${event.sequence}`}>
								<Table.Td>{event.sequence}</Table.Td>
								<Table.Td>{formatTimestamp(event.atUtc)}</Table.Td>
								<Table.Td>{t(`${keyPrefix}.kind.${kind === "Unknown" ? "unknown" : kind}`)}</Table.Td>
							</Table.Tr>
						);
					})}
				</Table.Tbody>
			</Table>

			{hasMore ? (
				<Button variant="default" onClick={loadMore} data-testid="external-app-events-load-more">
					{t(`${keyPrefix}.loadMore`)}
				</Button>
			) : null}
		</Stack>
	);
}
