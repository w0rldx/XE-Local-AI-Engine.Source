import {
	Alert,
	Badge,
	Button,
	Card,
	Container,
	Group,
	Loader,
	PasswordInput,
	Stack,
	Text,
	TextInput,
	Title,
} from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconRefresh, IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import type { ClearCloudSettingsResponse, SaveCloudSettingsResponse } from "@/core/api/generated";
import {
	clearCloudSettingsMutation,
	getCloudSettingsOptions,
	getCloudSettingsQueryKey,
	saveCloudSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toast } from "@/core/ui/notifications/Toast";
import { CodexSignInCard } from "@/features/cloud-settings/codex/components/CodexSignInCard";
import { type CloudSettingsFormValues, validateCloudSettingsForm } from "@/features/cloud-settings/models/CloudSettingsModel";

// The generated cloud-settings responses share one shape, so both the save and clear mutations resolve the same
// view; this alias keeps the onSuccess handlers readable.
type CloudSettings = SaveCloudSettingsResponse;

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected cloud settings error";
}

const emptyFormValues: CloudSettingsFormValues = {
	endpoint: "",
	apiKey: "",
	deploymentName: "",
};

export function CloudSettings() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const settingsQuery = useQuery(withResponseValidation(getCloudSettingsOptions()));
	const [formValues, setFormValues] = useState<CloudSettingsFormValues>(emptyFormValues);
	const [touchedFields, setTouchedFields] = useState<Partial<Record<keyof CloudSettingsFormValues, true>>>({});
	const [submitted, setSubmitted] = useState(false);

	// Tracks whether the Codex OAuth session is active (reported by CodexSignInCard).
	// When signed in, Codex is the active chat provider — no CloudSettings save needed.
	const [codexSignedIn, setCodexSignedIn] = useState(false);
	const handleCodexSignedInChange = useCallback((signedIn: boolean) => {
		setCodexSignedIn(signedIn);
	}, []);

	useEffect(() => {
		if (settingsQuery.data) {
			setFormValues({
				endpoint: settingsQuery.data.endpoint ?? "",
				apiKey: "",
				deploymentName: settingsQuery.data.deploymentName ?? "",
			});
			setTouchedFields({});
			setSubmitted(false);
		}
	}, [settingsQuery.data]);

	const errors = useMemo(() => validateCloudSettingsForm(formValues), [formValues]);
	const hasErrors = Object.keys(errors).length > 0;

	// Only expose an error for a field when the user has interacted with it or after a save attempt.
	const visibleErrors = useMemo(
		() =>
			submitted
				? errors
				: (Object.fromEntries(
						Object.entries(errors).filter(([key]) => touchedFields[key as keyof CloudSettingsFormValues]),
					) as Partial<Record<keyof CloudSettingsFormValues, string>>),
		[errors, touchedFields, submitted],
	);

	const saveMutation = useMutation({
		...withResponseValidation(saveCloudSettingsMutation()),
		onSuccess: async (settings: SaveCloudSettingsResponse) => {
			applySettingsResult(settings);
			setTouchedFields({});
			setSubmitted(false);
			queryClient.setQueryData(getCloudSettingsQueryKey(), settings);
			await queryClient.invalidateQueries({ queryKey: getCloudSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const clearMutation = useMutation({
		...withResponseValidation(clearCloudSettingsMutation()),
		onSuccess: async (settings: ClearCloudSettingsResponse) => {
			applySettingsResult(settings);
			queryClient.setQueryData(getCloudSettingsQueryKey(), settings);
			await queryClient.invalidateQueries({ queryKey: getCloudSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	// Save and clear return the same view; both reset the form to the stored (redacted) values and toast the
	// matching message. The API key is never echoed back, so it always resets to empty.
	function applySettingsResult(settings: CloudSettings): void {
		toast.success(
			settings.hasStoredApiKey ? "Cloud settings saved. Capability reporting was requested." : "Cloud settings cleared.",
		);
		setFormValues({
			endpoint: settings.endpoint ?? "",
			apiKey: "",
			deploymentName: settings.deploymentName ?? "",
		});
	}
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
					<Text c="dimmed">
						Store Azure OpenAI credentials locally for cloud-backed runtime mode. Saved API keys are never returned to this page.
					</Text>
				</Stack>

				{settingsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading cloud settings…</Text>
					</Group>
				) : null}

				{settingsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsQuery.error)}
					</Alert>
				) : null}

				{/* Persistent egress banner — shown whenever a Codex session is active */}
				{codexSignedIn ? (
					<Alert color="orange" icon={<IconAlertTriangle size={16} />}>
						<Text size="sm" fw={500}>
							{t("pages.cloudSettings.provider.activeEgressBanner")}
						</Text>
					</Alert>
				) : null}

				{/* Active provider indicator (read-only) — gated on cloud capability.
				    Priority: Codex session active > Azure credentials stored > None. */}
				{nodeCapabilities.cloudSettings ? (
					<Card withBorder={true} padding="md" radius="md">
						<Group justify="space-between" align="center">
							<Stack gap={2}>
								<Text fw={600}>{t("pages.cloudSettings.provider.label")}</Text>
								<Text size="sm" c="dimmed">
									{t("pages.cloudSettings.provider.egressNotice")}
								</Text>
							</Stack>
							{codexSignedIn ? (
								<Badge color="orange" variant="light" size="lg">
									{t("pages.cloudSettings.provider.codexOAuth")}
								</Badge>
							) : settings?.hasStoredApiKey ? (
								<Badge color="blue" variant="light" size="lg">
									{t("pages.cloudSettings.provider.azureFoundry")}
								</Badge>
							) : (
								<Badge color="gray" variant="light" size="lg">
									{t("pages.cloudSettings.provider.none")}
								</Badge>
							)}
						</Group>
					</Card>
				) : null}

				{/* Codex OAuth sign-in card — gated on cloud capability */}
				{nodeCapabilities.cloudSettings ? <CodexSignInCard onSignedInChange={handleCodexSignedInChange} /> : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>Azure OpenAI</Title>
							<Badge color={settings?.hasStoredApiKey ? "green" : "gray"}>
								{settings?.hasStoredApiKey ? "Configured" : "Not configured"}
							</Badge>
						</Group>
						<Text c="dimmed">
							Cloud credentials are encrypted on this worker. Enter the API key every time you save because stored keys are
							write-only.
						</Text>
						<TextInput
							label="Azure OpenAI endpoint"
							placeholder="https://example.openai.azure.com/"
							value={formValues.endpoint}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setFormValues((current) => ({ ...current, endpoint: value }));
							}}
							onBlur={() => setTouchedFields((current) => ({ ...current, endpoint: true }))}
							error={visibleErrors.endpoint}
						/>
						<TextInput
							label="Deployment name"
							placeholder="gpt-4o"
							value={formValues.deploymentName}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setFormValues((current) => ({ ...current, deploymentName: value }));
							}}
							onBlur={() => setTouchedFields((current) => ({ ...current, deploymentName: true }))}
							error={visibleErrors.deploymentName}
						/>
						<PasswordInput
							label="API key"
							description={
								settings?.hasStoredApiKey
									? "A key is stored. Enter a key to save or rotate cloud settings."
									: "The key is sent only to the local worker API."
							}
							value={formValues.apiKey}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setFormValues((current) => ({ ...current, apiKey: value }));
							}}
							onBlur={() => setTouchedFields((current) => ({ ...current, apiKey: true }))}
							error={visibleErrors.apiKey}
						/>
						<Group>
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								onClick={() => {
									setSubmitted(true);
									saveMutation.mutate({
										body: {
											providerName: "AzureFoundry",
											endpoint: formValues.endpoint.trim(),
											apiKey: formValues.apiKey.trim(),
											deploymentName: formValues.deploymentName.trim(),
										},
									});
								}}
								loading={saveMutation.isPending}
								disabled={hasErrors || isActionPending}
							>
								Save cloud settings
							</Button>
							<Button
								variant="outline"
								color="red"
								leftSection={<IconTrash size={16} />}
								onClick={() => clearMutation.mutate({})}
								loading={clearMutation.isPending}
								disabled={!settings?.hasStoredApiKey || isActionPending}
							>
								Clear saved credentials
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
			</Stack>
		</Container>
	);
}
