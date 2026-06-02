import { Alert, Badge, Button, Card, Container, Group, Loader, SimpleGrid, Stack, Table, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconHistory, IconPlayerPlay, IconRefresh } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { getInvocationMonitorOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toInvocationMonitor } from "@/features/invocations/models/InvocationMonitorMappers";
import type { InvocationCurrentDto, InvocationHistoryDto } from "@/features/invocations/models/InvocationMonitorModel";
import {
	formatInvocationDuration,
	formatInvocationText,
	formatInvocationTimestamp,
	getInvocationStatusColor,
	isInvocationActive,
	sortInvocationHistory,
} from "@/features/invocations/models/InvocationMonitorModel";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Invocation monitor data could not be loaded.";
}

function CurrentInvocation({ current }: { readonly current: InvocationCurrentDto | null }) {
	if (!current) {
		return (
			<Card withBorder={true} radius="md" p="lg">
				<Stack gap="sm">
					<Group justify="space-between">
						<Title order={3}>Current invocation</Title>
						<IconPlayerPlay size={22} />
					</Group>
					<Text c="dimmed">No invocation is currently assigned or running.</Text>
				</Stack>
			</Card>
		);
	}

	return (
		<Card withBorder={true} radius="md" p="lg">
			<Stack gap="md">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Title order={3}>Current invocation</Title>
						<Text size="sm" c="dimmed" style={{ wordBreak: "break-all" }}>
							{current.invocationId}
						</Text>
					</Stack>
					<Badge color={getInvocationStatusColor(current.status)}>{current.status}</Badge>
				</Group>
				<SimpleGrid cols={{ base: 1, md: 2 }} spacing="sm">
					<Text>Model: {formatInvocationText(current.modelUsed)}</Text>
					<Text>Conversation: {current.conversationId}</Text>
					<Text>Started: {formatInvocationTimestamp(current.startedAt)}</Text>
					<Text>Updated: {formatInvocationTimestamp(current.lastUpdatedAt)}</Text>
					<Text>Output chunks: {current.streamedChunkCount}</Text>
					<Text>Thinking chunks: {current.streamedThinkingChunkCount}</Text>
					<Text>Pending tool calls: {current.pendingToolCallCount}</Text>
					<Text>Pending approval: {current.hasPendingApproval ? "Yes" : "No"}</Text>
				</SimpleGrid>
				{current.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{current.error}
					</Alert>
				) : null}
			</Stack>
		</Card>
	);
}

function HistoryRows({ history }: { readonly history: InvocationHistoryDto[] }) {
	if (history.length === 0) {
		return (
			<Table.Tr>
				<Table.Td colSpan={8}>
					<Text c="dimmed">No completed invocations recorded yet.</Text>
				</Table.Td>
			</Table.Tr>
		);
	}

	return history.map((entry) => (
		<Table.Tr key={entry.invocationId}>
			<Table.Td>
				<Text size="sm" style={{ wordBreak: "break-all" }}>
					{entry.invocationId}
				</Text>
			</Table.Td>
			<Table.Td>
				<Badge color={getInvocationStatusColor(entry.status)}>{entry.status}</Badge>
			</Table.Td>
			<Table.Td>{formatInvocationText(entry.modelUsed)}</Table.Td>
			<Table.Td>{formatInvocationTimestamp(entry.completedAt)}</Table.Td>
			<Table.Td>{formatInvocationDuration(entry.durationMs)}</Table.Td>
			<Table.Td>{entry.streamedChunkCount}</Table.Td>
			<Table.Td>{entry.streamedThinkingChunkCount}</Table.Td>
			<Table.Td>{formatInvocationText(entry.error ?? entry.failureCategory)}</Table.Td>
		</Table.Tr>
	));
}

export function Invocations() {
	const monitorQuery = useQuery({
		...withResponseValidation(getInvocationMonitorOptions()),
		refetchInterval: 5000,
		select: toInvocationMonitor,
	});
	const monitor = monitorQuery.data;
	const history = useMemo(() => sortInvocationHistory(monitor?.history ?? []), [monitor]);
	const active = isInvocationActive(monitor?.current?.status);

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							Worker Node
						</Text>
						<Title order={2}>Invocation monitor</Title>
						<Text c="dimmed">Inspect the active invocation and the local in-memory history retained by the worker.</Text>
					</Stack>
					<Group gap="sm">
						<Badge color={active ? "blue" : "gray"}>{active ? "Active" : "Idle"}</Badge>
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => monitorQuery.refetch()}
							disabled={monitorQuery.isFetching}
						>
							Refresh
						</Button>
					</Group>
				</Group>

				{monitorQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading invocation monitor…</Text>
					</Group>
				) : null}

				{monitorQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(monitorQuery.error)}
					</Alert>
				) : null}

				<CurrentInvocation current={monitor?.current ?? null} />

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between">
							<Stack gap={2}>
								<Title order={3}>Invocation history</Title>
								<Text size="sm" c="dimmed">
									Showing up to {monitor?.historyCapacity ?? 0} most recent terminal invocations.
								</Text>
							</Stack>
							<IconHistory size={22} />
						</Group>
						<Table.ScrollContainer minWidth={980}>
							<Table striped={true} highlightOnHover={true} verticalSpacing="sm">
								<Table.Thead>
									<Table.Tr>
										<Table.Th>Invocation</Table.Th>
										<Table.Th>Status</Table.Th>
										<Table.Th>Model</Table.Th>
										<Table.Th>Completed</Table.Th>
										<Table.Th>Duration</Table.Th>
										<Table.Th>Chunks</Table.Th>
										<Table.Th>Thinking</Table.Th>
										<Table.Th>Result</Table.Th>
									</Table.Tr>
								</Table.Thead>
								<Table.Tbody>
									<HistoryRows history={history} />
								</Table.Tbody>
							</Table>
						</Table.ScrollContainer>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
