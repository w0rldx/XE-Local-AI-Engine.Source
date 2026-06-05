import { Alert, Button, Card, Container, Group, Loader, NumberInput, Stack, Switch, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCode, IconDeviceFloppy, IconRefresh, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type { SaveNodeSettingsResponse } from "@/core/api/generated";
import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import {
	type NodeSettingsTimeoutInput,
	nodeSettingsDefaults,
	toValidNodeSettingsTimeoutSeconds,
} from "@/features/node-settings/models/NodeSettingsModel";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected node settings error";
}

export function NodeSettings() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const settingsQuery = useQuery(withResponseValidation(getNodeSettingsOptions()));
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const { toggle: toggleDeveloperMode } = useDeveloperModeStore((state) => state.actions);
	const settings = settingsQuery.data;
	const [timeoutSeconds, setTimeoutSeconds] = useState<NodeSettingsTimeoutInput>(
		nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
	);

	useEffect(() => {
		if (settings?.maxMessageRequestTimeoutSeconds !== undefined) {
			setTimeoutSeconds(settings.maxMessageRequestTimeoutSeconds);
		}
	}, [settings]);

	const minTimeout = settings?.minMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.minMessageRequestTimeoutSeconds;
	const maxTimeout =
		settings?.maxAllowedMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxAllowedMessageRequestTimeoutSeconds;
	const timeoutToSave = useMemo(
		() => toValidNodeSettingsTimeoutSeconds(timeoutSeconds, minTimeout, maxTimeout),
		[maxTimeout, minTimeout, timeoutSeconds],
	);

	const saveMutation = useMutation({
		...withResponseValidation(saveNodeSettingsMutation()),
		onSuccess: async (updatedSettings: SaveNodeSettingsResponse) => {
			toast.success("Node settings saved. Capability reporting was requested for the worker connection.");
			setTimeoutSeconds(
				updatedSettings.maxMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
			);
			queryClient.setQueryData(getNodeSettingsQueryKey(), updatedSettings);
			await queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const canSave = timeoutToSave !== undefined && !saveMutation.isPending;

	return (
		<Container fluid={true} py="lg">
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
						<Text c="dimmed">Loading node settings…</Text>
					</Group>
				) : null}

				{settingsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsQuery.error)}
					</Alert>
				) : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>Local chat runtime</Title>
							<IconSettings size={22} />
						</Group>
						<Text c="dimmed">
							The maximum message request timeout is included in capability reports so the platform can respect this worker's
							local runtime limit.
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
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								onClick={() =>
									saveMutation.mutate({
										body: {
											maxMessageRequestTimeoutSeconds:
												timeoutToSave ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
										},
									})
								}
								loading={saveMutation.isPending}
								disabled={!canSave}
							>
								Save settings
							</Button>
							<Button
								variant="subtle"
								leftSection={<IconRefresh size={16} />}
								onClick={() => settingsQuery.refetch()}
								disabled={settingsQuery.isFetching}
							>
								Reload
							</Button>
						</Group>
					</Stack>
				</Card>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>{t("pages.nodeSettings.developerMode.title", "Developer settings")}</Title>
							<IconCode size={22} />
						</Group>
						<Switch
							label={t("pages.nodeSettings.developerMode.label", "Developer mode")}
							description={t(
								"pages.nodeSettings.developerMode.description",
								"Enables advanced, experimental controls in the app (e.g. chat sampling options). Stored in this browser only.",
							)}
							checked={developerMode}
							onChange={() => toggleDeveloperMode()}
							data-testid="developer-mode-switch"
						/>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
