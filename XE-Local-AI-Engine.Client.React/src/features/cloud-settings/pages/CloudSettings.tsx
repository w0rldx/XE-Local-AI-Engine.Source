import { Alert, Badge, Button, Card, Container, Group, Loader, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconCloud, IconDeviceFloppy, IconRefresh, IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";

import { clearCloudSettings, type CloudSettingsDto, getCloudSettings, saveCloudSettings } from "@/features/cloud-settings/api/CloudSettingsApi";
import { type CloudSettingsFormValues, validateCloudSettingsForm } from "@/features/cloud-settings/models/CloudSettingsModel";
import { cloudSettingsQueryKeys } from "@/features/cloud-settings/queries/CloudSettingsQueryKeys";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected cloud settings error";
}

const emptyFormValues: CloudSettingsFormValues = {
	endpoint: "",
	apiKey: "",
	deploymentName: "",
};

export function CloudSettings() {
	const queryClient = useQueryClient();
	const settingsQuery = useQuery({
		queryKey: cloudSettingsQueryKeys.settings(),
		queryFn: ({ signal }) => getCloudSettings({ signal }),
	});
	const [formValues, setFormValues] = useState<CloudSettingsFormValues>(emptyFormValues);
	const [message, setMessage] = useState<string | undefined>();

	useEffect(() => {
		if (settingsQuery.data) {
			setFormValues({
				endpoint: settingsQuery.data.endpoint ?? "",
				apiKey: "",
				deploymentName: settingsQuery.data.deploymentName ?? "",
			});
		}
	}, [settingsQuery.data]);

	const errors = useMemo(() => validateCloudSettingsForm(formValues), [formValues]);
	const hasErrors = Object.keys(errors).length > 0;

	const applySettings = useCallback(
		async (settings: CloudSettingsDto) => {
			setMessage(settings.hasStoredApiKey ? "Cloud settings saved. Capability reporting was requested." : "Cloud settings cleared.");
			setFormValues({
				endpoint: settings.endpoint ?? "",
				apiKey: "",
				deploymentName: settings.deploymentName ?? "",
			});
			queryClient.setQueryData(cloudSettingsQueryKeys.settings(), settings);
			await queryClient.invalidateQueries({ queryKey: cloudSettingsQueryKeys.settings() });
		},
		[queryClient],
	);

	const saveMutation = useMutation({
		mutationFn: () =>
			saveCloudSettings({
				providerName: "AzureFoundry",
				endpoint: formValues.endpoint.trim(),
				apiKey: formValues.apiKey.trim(),
				deploymentName: formValues.deploymentName.trim(),
			}),
		onSuccess: applySettings,
	});

	const clearMutation = useMutation({ mutationFn: () => clearCloudSettings(), onSuccess: applySettings });
	const actionError = saveMutation.error ?? clearMutation.error;
	const isActionPending = saveMutation.isPending || clearMutation.isPending;
	const settings = settingsQuery.data;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						Worker Node
					</Text>
					<Title order={2}>Cloud settings</Title>
					<Text c="dimmed">Store Azure OpenAI credentials locally for cloud-backed runtime mode. Saved API keys are never returned to this page.</Text>
				</Stack>

				{settingsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading cloud settings...</Text>
					</Group>
				) : null}

				{settingsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsQuery.error)}
					</Alert>
				) : null}

				{actionError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(actionError)}
					</Alert>
				) : null}

				{message ? <Alert color="green">{message}</Alert> : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>Azure OpenAI</Title>
							<Badge color={settings?.hasStoredApiKey ? "green" : "gray"}>{settings?.hasStoredApiKey ? "Configured" : "Not configured"}</Badge>
						</Group>
						<Text c="dimmed">
							Cloud credentials are encrypted on this worker. Enter the API key every time you save because stored keys are write-only.
						</Text>
						<TextInput
							label="Azure OpenAI endpoint"
							placeholder="https://example.openai.azure.com/"
							value={formValues.endpoint}
							onChange={(event) => setFormValues((current) => ({ ...current, endpoint: event.currentTarget.value }))}
							error={errors.endpoint}
						/>
						<TextInput
							label="Deployment name"
							placeholder="gpt-4o"
							value={formValues.deploymentName}
							onChange={(event) => setFormValues((current) => ({ ...current, deploymentName: event.currentTarget.value }))}
							error={errors.deploymentName}
						/>
						<PasswordInput
							label="API key"
							description={settings?.hasStoredApiKey ? "A key is stored. Enter a key to save or rotate cloud settings." : "The key is sent only to the local worker API."}
							value={formValues.apiKey}
							onChange={(event) => setFormValues((current) => ({ ...current, apiKey: event.currentTarget.value }))}
							error={errors.apiKey}
						/>
						<Group>
							<Button leftSection={<IconDeviceFloppy size={16} />} onClick={() => saveMutation.mutate()} loading={saveMutation.isPending} disabled={hasErrors || isActionPending}>
								Save cloud settings
							</Button>
							<Button variant="outline" color="red" leftSection={<IconTrash size={16} />} onClick={() => clearMutation.mutate()} loading={clearMutation.isPending} disabled={!settings?.hasStoredApiKey || isActionPending}>
								Clear saved credentials
							</Button>
							<Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={() => settingsQuery.refetch()} disabled={settingsQuery.isFetching}>
								Reload
							</Button>
						</Group>
						<Group gap="xs">
							<IconCloud size={16} />
							<Text size="sm" c="dimmed">
								Provider: AzureFoundry. Runtime provider switching is not changed by this page.
							</Text>
						</Group>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
