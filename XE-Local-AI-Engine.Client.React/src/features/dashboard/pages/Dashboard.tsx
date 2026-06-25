import { Alert, Badge, Button, Card, Container, Group, Loader, SimpleGrid, Stack, Table, Text, Title } from "@mantine/core";
import {
	IconAlertTriangle,
	IconPlugConnected,
	IconPlugConnectedX,
	IconRefresh,
	IconSettingsAutomation,
} from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsConnectionV1ConnectionStatusResponse } from "@/core/api/generated";
import {
	connectConnectionMutation,
	disableAutoConnectMutation,
	disconnectConnectionMutation,
	enableAutoConnectMutation,
	getConnectionStatusOptions,
	getConnectionStatusQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toast } from "@/core/ui/notifications/Toast";
import { toConnectionStatusViewModel } from "@/features/dashboard/models/ConnectionMappers";
import { type ConnectionStatusViewModel, connectionStatusColor } from "@/features/dashboard/models/ConnectionStatusModel";

function isTokenExpired(tokenExpiresAt?: string | null): boolean {
	if (!tokenExpiresAt) {
		return false;
	}
	const date = new Date(tokenExpiresAt);
	return !Number.isNaN(date.getTime()) && date < new Date();
}

function formatOptionalDateLocalized(value?: string | null): string {
	if (!value) {
		return "";
	}
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

export function Dashboard() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { data: status, isLoading: statusIsLoading, error: statusError, refetch: statusRefetch, isFetching: statusIsFetching } = useQuery({
		...withResponseValidation(getConnectionStatusOptions()),
		select: toConnectionStatusViewModel,
		refetchInterval: 5000,
	});

	// Every connection action returns the fresh wire status; cache it under the generated status query key (the
	// `select` maps it to the view model) and invalidate so the polling query picks up the change immediately.
	const onActionSuccess = async (status: XeLocalAiEngineClientEndpointsConnectionV1ConnectionStatusResponse) => {
		queryClient.setQueryData(getConnectionStatusQueryKey(), status);
		await queryClient.invalidateQueries({ queryKey: getConnectionStatusQueryKey() });
	};

	// Connection action failures surface as a transient toast (the kept inline Alerts are the persistent
	// status-load error and the connection's lastError).
	const onActionError = (error: unknown): void => {
		toast.error(error instanceof Error ? error.message : t("pages.dashboard.unexpectedError"));
	};

	const connectMutation = useMutation({
		...withResponseValidation(connectConnectionMutation()),
		onSuccess: onActionSuccess,
		onError: onActionError,
	});
	const disconnectMutation = useMutation({
		...withResponseValidation(disconnectConnectionMutation()),
		onSuccess: onActionSuccess,
		onError: onActionError,
	});
	const enableAutoConnectMutationResult = useMutation({
		...withResponseValidation(enableAutoConnectMutation()),
		onSuccess: onActionSuccess,
		onError: onActionError,
	});
	const disableAutoConnectMutationResult = useMutation({
		...withResponseValidation(disableAutoConnectMutation()),
		onSuccess: onActionSuccess,
		onError: onActionError,
	});

	const isActionPending =
		connectMutation.isPending ||
		disconnectMutation.isPending ||
		enableAutoConnectMutationResult.isPending ||
		disableAutoConnectMutationResult.isPending;

	const getErrorMessage = (error: unknown): string =>
		error instanceof Error ? error.message : t("pages.dashboard.unexpectedError");

	const getConnectionStatusLabel = (state: string): string => {
		const key = `pages.dashboard.connectionStatus.${state}`;
		return t(key, { defaultValue: t("pages.dashboard.connectionStatus.unknown") });
	};

	const getStatusSummary = (s: ConnectionStatusViewModel): string => {
		if (!s.isPaired) {
			return t("pages.dashboard.notPairedHint");
		}
		if (s.state === "reconnecting") {
			return t("pages.dashboard.connectionHint.reconnecting");
		}
		if (s.autoConnectOnStart) {
			return t("pages.dashboard.connectionHint.autoConnectEnabled");
		}
		return t("pages.dashboard.connectionHint.autoConnectDisabled");
	};

	const tokenExpired = status ? isTokenExpired(status.tokenExpiresAt) : false;
	const tokenDisplay = status?.tokenExpiresAt
		? formatOptionalDateLocalized(status.tokenExpiresAt)
		: t("pages.dashboard.nodeCredentials.notAvailable");

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						{t("pages.dashboard.eyebrow")}
					</Text>
					<Title order={2}>{t("pages.dashboard.title")}</Title>
					<Text c="dimmed">{t("pages.dashboard.subtitle")}</Text>
				</Stack>

				{statusIsLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.dashboard.loadingStatus")}</Text>
					</Group>
				) : null}

				{statusError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{getErrorMessage(statusError)}
					</Alert>
				) : null}

				{status ? (
					<SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Group justify="space-between" align="center">
									<Title order={3}>{t("pages.dashboard.platformConnection.title")}</Title>
									<Badge color={connectionStatusColor(status.state)}>{getConnectionStatusLabel(status.state)}</Badge>
								</Group>
								<Text c="dimmed">{getStatusSummary(status)}</Text>

								{status.lastError ? (
									<Alert color="red" icon={<IconAlertTriangle size={16} />}>
										{status.lastError}
									</Alert>
								) : null}

								<Group>
									<Button
										leftSection={<IconPlugConnected size={16} />}
										onClick={() => connectMutation.mutate({})}
										loading={connectMutation.isPending}
										disabled={!status.canConnect || isActionPending}
									>
										{t("pages.dashboard.platformConnection.connect")}
									</Button>
									<Button
										variant="outline"
										leftSection={<IconPlugConnectedX size={16} />}
										onClick={() => disconnectMutation.mutate({})}
										loading={disconnectMutation.isPending}
										disabled={!status.canDisconnect || isActionPending}
									>
										{t("pages.dashboard.platformConnection.disconnect")}
									</Button>
									<Button
										variant="subtle"
										leftSection={<IconRefresh size={16} />}
										onClick={() => statusRefetch()}
										disabled={statusIsFetching}
									>
										{t("pages.dashboard.platformConnection.refresh")}
									</Button>
								</Group>
							</Stack>
						</Card>

						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Group justify="space-between" align="center">
									<Title order={3}>{t("pages.dashboard.startupConnection.title")}</Title>
									<Badge color={status.autoConnectOnStart ? "green" : "gray"}>
										{status.autoConnectOnStart
											? t("pages.dashboard.startupConnection.enabled")
											: t("pages.dashboard.startupConnection.disabled")}
									</Badge>
								</Group>
								<Text c="dimmed">{t("pages.dashboard.startupConnection.hint")}</Text>
								<Group>
									<Button
										leftSection={<IconSettingsAutomation size={16} />}
										onClick={() => enableAutoConnectMutationResult.mutate({})}
										loading={enableAutoConnectMutationResult.isPending}
										disabled={!status.canEnableAutoConnect || isActionPending}
									>
										{t("pages.dashboard.startupConnection.enableAutoConnect")}
									</Button>
									<Button
										variant="outline"
										onClick={() => disableAutoConnectMutationResult.mutate({})}
										loading={disableAutoConnectMutationResult.isPending}
										disabled={!status.canDisableAutoConnect || isActionPending}
									>
										{t("pages.dashboard.startupConnection.disableAutoConnect")}
									</Button>
								</Group>
							</Stack>
						</Card>

						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Title order={3}>{t("pages.dashboard.nodeCredentials.title")}</Title>
								<Table withTableBorder={true} withColumnBorders={true}>
									<Table.Tbody>
										<Table.Tr>
											<Table.Th>{t("pages.dashboard.nodeCredentials.binding")}</Table.Th>
											<Table.Td>
												{status.isPaired
													? t("pages.dashboard.nodeCredentials.paired")
													: t("pages.dashboard.nodeCredentials.notPaired")}
											</Table.Td>
										</Table.Tr>
										<Table.Tr>
											<Table.Th>{t("pages.dashboard.nodeCredentials.bindingMethod")}</Table.Th>
											<Table.Td>
												{status.bindingMethod ?? t("pages.dashboard.nodeCredentials.notAvailable")}
											</Table.Td>
										</Table.Tr>
										<Table.Tr>
											<Table.Th>{t("pages.dashboard.nodeCredentials.nodeName")}</Table.Th>
											<Table.Td>
												{status.lastKnownNodeName ?? t("pages.dashboard.nodeCredentials.notAvailable")}
											</Table.Td>
										</Table.Tr>
										<Table.Tr>
											<Table.Th>{t("pages.dashboard.nodeCredentials.tokenExpires")}</Table.Th>
											<Table.Td>
												<Group gap="xs" wrap="nowrap">
													<span>{tokenDisplay}</span>
													{tokenExpired ? (
														<Badge color="red" size="sm">
															{t("pages.dashboard.nodeCredentials.tokenExpiredBadge")}
														</Badge>
													) : null}
												</Group>
											</Table.Td>
										</Table.Tr>
									</Table.Tbody>
								</Table>
							</Stack>
						</Card>

						<Card withBorder={true} radius="md" p="lg">
							<Stack gap="md">
								<Title order={3}>{t("pages.dashboard.lastUpdate.title")}</Title>
								<Text>
									{status.lastUpdatedAt
										? formatOptionalDateLocalized(status.lastUpdatedAt)
										: t("pages.dashboard.nodeCredentials.notAvailable")}
								</Text>
								<Text size="sm" c="dimmed">
									{t("pages.dashboard.lastUpdate.privacyNote")}
								</Text>
							</Stack>
						</Card>
					</SimpleGrid>
				) : null}
			</Stack>
		</Container>
	);
}
