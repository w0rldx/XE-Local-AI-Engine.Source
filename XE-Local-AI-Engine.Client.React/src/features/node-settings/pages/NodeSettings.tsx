import { Alert, Button, Card, Container, Group, Loader, NumberInput, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconRefresh, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";

import { getNodeSettings, type NodeSettingsDto, saveNodeSettings } from "@/features/node-settings/api/NodeSettingsApi";
import {
	nodeSettingsDefaults,
	type NodeSettingsTimeoutInput,
	toValidNodeSettingsTimeoutSeconds,
} from "@/features/node-settings/models/NodeSettingsModel";
import { nodeSettingsQueryKeys } from "@/features/node-settings/queries/NodeSettingsQueryKeys";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected node settings error";
}

export function NodeSettings() {
	const queryClient = useQueryClient();
	const settingsQuery = useQuery({
		queryKey: nodeSettingsQueryKeys.settings(),
		queryFn: ({ signal }) => getNodeSettings({ signal }),
	});
	const settings = settingsQuery.data;
	const [timeoutSeconds, setTimeoutSeconds] = useState<NodeSettingsTimeoutInput>(nodeSettingsDefaults.maxMessageRequestTimeoutSeconds);
	const [message, setMessage] = useState<string | undefined>();

	useEffect(() => {
		if (settings) {
			setTimeoutSeconds(settings.maxMessageRequestTimeoutSeconds);
		}
	}, [settings]);

	const minTimeout = settings?.minMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.minMessageRequestTimeoutSeconds;
	const maxTimeout = settings?.maxAllowedMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxAllowedMessageRequestTimeoutSeconds;
	const timeoutToSave = useMemo(() => toValidNodeSettingsTimeoutSeconds(timeoutSeconds, minTimeout, maxTimeout), [maxTimeout, minTimeout, timeoutSeconds]);

	const applySettings = useCallback(
		async (updatedSettings: NodeSettingsDto) => {
			setMessage("Node settings saved. Capability reporting was requested for the worker connection.");
			setTimeoutSeconds(updatedSettings.maxMessageRequestTimeoutSeconds);
			queryClient.setQueryData(nodeSettingsQueryKeys.settings(), updatedSettings);
			await queryClient.invalidateQueries({ queryKey: nodeSettingsQueryKeys.settings() });
		},
		[queryClient],
	);

	const saveMutation = useMutation({
		mutationFn: () => saveNodeSettings({ maxMessageRequestTimeoutSeconds: timeoutToSave ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds }),
		onSuccess: applySettings,
	});

	const canSave = timeoutToSave !== undefined && !saveMutation.isPending;

	return (
		<Container size="md" py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						Worker Node
					</Text>
					<Title order={2}>Node settings</Title>
					<Text c="dimmed">Tune non-secret local runtime settings stored on this worker.</Text>
				</Stack>

				{settingsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading node settings...</Text>
					</Group>
				) : null}

				{settingsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsQuery.error)}
					</Alert>
				) : null}

				{saveMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(saveMutation.error)}
					</Alert>
				) : null}

				{message ? <Alert color="green">{message}</Alert> : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>Local chat runtime</Title>
							<IconSettings size={22} />
						</Group>
						<Text c="dimmed">
							The maximum message request timeout is included in capability reports so the platform can respect this worker's local runtime limit.
						</Text>
						<NumberInput
							label="Maximum message request timeout"
							description={`Allowed range: ${minTimeout}–${maxTimeout} seconds.`}
							suffix=" seconds"
							min={minTimeout}
							max={maxTimeout}
							step={5}
							allowDecimal={false}
							value={timeoutSeconds}
							onChange={setTimeoutSeconds}
							error={timeoutToSave === undefined ? `Enter a whole number from ${minTimeout} to ${maxTimeout}.` : undefined}
						/>
						<Group>
							<Button leftSection={<IconDeviceFloppy size={16} />} onClick={() => saveMutation.mutate()} loading={saveMutation.isPending} disabled={!canSave}>
								Save settings
							</Button>
							<Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={() => settingsQuery.refetch()} disabled={settingsQuery.isFetching}>
								Reload
							</Button>
						</Group>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
