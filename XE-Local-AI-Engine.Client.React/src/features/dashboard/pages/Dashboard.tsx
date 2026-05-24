import { Alert, Badge, Button, Card, Container, Group, Loader, SimpleGrid, Stack, Table, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlugConnected, IconPlugConnectedX, IconRefresh, IconSettingsAutomation } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	connectWorker,
	disableAutoConnect,
	disconnectWorker,
	enableAutoConnect,
	getConnectionStatus,
	type ConnectionStatusDto,
} from "@/features/dashboard/api/ConnectionApi";
import { connectionActionHint, connectionStatusColor, connectionStatusLabel, formatOptionalDate } from "@/features/dashboard/models/ConnectionStatusModel";
import { connectionQueryKeys } from "@/features/dashboard/queries/ConnectionQueryKeys";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected connection action error";
}

function statusSummary(status: ConnectionStatusDto): string {
	if (!status.isPaired) {
		return "Bind this node before connecting to the Central Platform.";
	}

	return connectionActionHint(status.state, status.autoConnectOnStart);
}

export function Dashboard() {
	const queryClient = useQueryClient();
	const statusQuery = useQuery({
		queryKey: connectionQueryKeys.status(),
		queryFn: ({ signal }) => getConnectionStatus({ signal }),
		refetchInterval: 5000,
	});

	const applyStatus = useCallback(
		async (status: ConnectionStatusDto) => {
			queryClient.setQueryData(connectionQueryKeys.status(), status);
			await queryClient.invalidateQueries({ queryKey: connectionQueryKeys.status() });
		},
		[queryClient],
	);

	const connectMutation = useMutation({ mutationFn: () => connectWorker(), onSuccess: applyStatus });
	const disconnectMutation = useMutation({ mutationFn: () => disconnectWorker(), onSuccess: applyStatus });
	const enableAutoConnectMutation = useMutation({ mutationFn: () => enableAutoConnect(), onSuccess: applyStatus });
	const disableAutoConnectMutation = useMutation({ mutationFn: () => disableAutoConnect(), onSuccess: applyStatus });

	const status = statusQuery.data;
	const actionError = connectMutation.error ?? disconnectMutation.error ?? enableAutoConnectMutation.error ?? disableAutoConnectMutation.error;
	const isActionPending =
		connectMutation.isPending || disconnectMutation.isPending || enableAutoConnectMutation.isPending || disableAutoConnectMutation.isPending;

	return (
		<Container size="lg" py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						Worker Node
					</Text>
					<Title order={2}>Dashboard</Title>
					<Text c="dimmed">Monitor the local worker connection and control startup connection behavior.</Text>
				</Stack>

				{statusQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading connection status...</Text>
					</Group>
				) : null}

				{statusQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(statusQuery.error)}
					</Alert>
				) : null}

				{actionError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(actionError)}
					</Alert>
				) : null}

				{status ? (
					<SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Group justify="space-between" align="center">
									<Title order={3}>Platform connection</Title>
									<Badge color={connectionStatusColor(status.state)}>{connectionStatusLabel(status.state)}</Badge>
								</Group>
								<Text c="dimmed">{statusSummary(status)}</Text>

								{status.lastError ? (
									<Alert color="red" icon={<IconAlertTriangle size={16} />}>
										{status.lastError}
									</Alert>
								) : null}

								<Group>
									<Button
										leftSection={<IconPlugConnected size={16} />}
										onClick={() => connectMutation.mutate()}
										loading={connectMutation.isPending}
										disabled={!status.canConnect || isActionPending}
									>
										Connect
									</Button>
									<Button
										variant="outline"
										leftSection={<IconPlugConnectedX size={16} />}
										onClick={() => disconnectMutation.mutate()}
										loading={disconnectMutation.isPending}
										disabled={!status.canDisconnect || isActionPending}
									>
										Disconnect
									</Button>
									<Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={() => statusQuery.refetch()} disabled={statusQuery.isFetching}>
										Refresh
									</Button>
								</Group>
							</Stack>
						</Card>

						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Group justify="space-between" align="center">
									<Title order={3}>Startup connection</Title>
									<Badge color={status.autoConnectOnStart ? "green" : "gray"}>{status.autoConnectOnStart ? "Enabled" : "Disabled"}</Badge>
								</Group>
								<Text c="dimmed">Auto-connect stays disabled by default after binding until you explicitly enable it.</Text>
								<Group>
									<Button
										leftSection={<IconSettingsAutomation size={16} />}
										onClick={() => enableAutoConnectMutation.mutate()}
										loading={enableAutoConnectMutation.isPending}
										disabled={!status.canEnableAutoConnect || isActionPending}
									>
										Enable auto-connect
									</Button>
									<Button
										variant="outline"
										onClick={() => disableAutoConnectMutation.mutate()}
										loading={disableAutoConnectMutation.isPending}
										disabled={!status.canDisableAutoConnect || isActionPending}
									>
										Disable auto-connect
									</Button>
								</Group>
							</Stack>
						</Card>

						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Title order={3}>Node credentials</Title>
								<Table withTableBorder={true} withColumnBorders={true}>
									<Table.Tbody>
										<Table.Tr>
											<Table.Th>Binding</Table.Th>
											<Table.Td>{status.isPaired ? "Paired" : "Not paired"}</Table.Td>
										</Table.Tr>
										<Table.Tr>
											<Table.Th>Binding method</Table.Th>
											<Table.Td>{status.bindingMethod ?? "Not available"}</Table.Td>
										</Table.Tr>
										<Table.Tr>
											<Table.Th>Node name</Table.Th>
											<Table.Td>{status.lastKnownNodeName ?? "Not available"}</Table.Td>
										</Table.Tr>
										<Table.Tr>
											<Table.Th>Token expires</Table.Th>
											<Table.Td>{formatOptionalDate(status.tokenExpiresAt)}</Table.Td>
										</Table.Tr>
									</Table.Tbody>
								</Table>
							</Stack>
						</Card>

						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Title order={3}>Last update</Title>
								<Text>{formatOptionalDate(status.lastUpdatedAt)}</Text>
								<Text size="sm" c="dimmed">
									Connection controls never return access or refresh tokens to the browser.
								</Text>
							</Stack>
						</Card>
					</SimpleGrid>
				) : null}
			</Stack>
		</Container>
	);
}
