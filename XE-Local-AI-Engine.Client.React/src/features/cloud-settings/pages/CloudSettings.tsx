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
import { useCallback, useEffect, useMemo, useReducer, useState } from "react";
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

// Save and clear return the same view; both reset the form to the stored (redacted) values. The API key is never
// echoed back, so it always resets to empty. Module-scoped because it uses no component state.
function settingsToFormValues(settings: CloudSettings): CloudSettingsFormValues {
	return {
		endpoint: settings.endpoint ?? "",
		apiKey: "",
		deploymentName: settings.deploymentName ?? "",
	};
}

function toastSettingsResult(settings: CloudSettings): void {
	toast.success(
		settings.hasStoredApiKey ? "Cloud settings saved. Capability reporting was requested." : "Cloud settings cleared.",
	);
}

const emptyFormValues: CloudSettingsFormValues = {
	endpoint: "",
	apiKey: "",
	deploymentName: "",
};

// The form values, the per-field "touched" map, and the submit flag always reset together when the
// stored settings load or a save/clear completes. Grouping them under one reducer lets a single
// dispatch reset all three at once, replacing the cascading set-state calls that previously fired
// three separate updates from one effect.
interface FormState {
	values: CloudSettingsFormValues;
	touched: Partial<Record<keyof CloudSettingsFormValues, true>>;
	submitted: boolean;
}

type FormAction =
	| { type: "reset"; values: CloudSettingsFormValues }
	| { type: "setValues"; values: CloudSettingsFormValues }
	| { type: "setField"; field: keyof CloudSettingsFormValues; value: string }
	| { type: "touchField"; field: keyof CloudSettingsFormValues }
	| { type: "submit" };

const initialFormState: FormState = {
	values: emptyFormValues,
	touched: {},
	submitted: false,
};

function formReducer(state: FormState, action: FormAction): FormState {
	switch (action.type) {
		// Loading stored settings and a successful save both replace the values and clear the
		// touched/submitted interaction flags in one step.
		case "reset":
			return { values: action.values, touched: {}, submitted: false };
		// Clearing credentials replaces only the values, leaving any existing interaction flags intact
		// (matches the original clear handler, which never reset touched/submitted).
		case "setValues":
			return { ...state, values: action.values };
		case "setField":
			return { ...state, values: { ...state.values, [action.field]: action.value } };
		case "touchField":
			return { ...state, touched: { ...state.touched, [action.field]: true } };
		case "submit":
			return { ...state, submitted: true };
		default:
			return state;
	}
}

export function CloudSettings() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { data: settingsData, isLoading: settingsIsLoading, error: settingsError, refetch: settingsRefetch, isFetching: settingsIsFetching } = useQuery(withResponseValidation(getCloudSettingsOptions()));
	const [formState, dispatch] = useReducer(formReducer, initialFormState);
	const { values: formValues, touched: touchedFields, submitted } = formState;

	// Tracks whether the Codex OAuth session is active (reported by CodexSignInCard).
	// When signed in, Codex is the active chat provider — no CloudSettings save needed.
	const [codexSignedIn, setCodexSignedIn] = useState(false);
	const handleCodexSignedInChange = useCallback((signedIn: boolean) => {
		setCodexSignedIn(signedIn);
	}, []);

	useEffect(() => {
		if (settingsData) {
			dispatch({
				type: "reset",
				values: {
					endpoint: settingsData.endpoint ?? "",
					apiKey: "",
					deploymentName: settingsData.deploymentName ?? "",
				},
			});
		}
	}, [settingsData]);

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
			// A successful save resets the values and clears the touched/submitted flags together.
			dispatch({ type: "reset", values: settingsToFormValues(settings) });
			toastSettingsResult(settings);
			queryClient.setQueryData(getCloudSettingsQueryKey(), settings);
			await queryClient.invalidateQueries({ queryKey: getCloudSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const clearMutation = useMutation({
		...withResponseValidation(clearCloudSettingsMutation()),
		onSuccess: async (settings: ClearCloudSettingsResponse) => {
			// Clearing only replaces the values; the touched/submitted flags are left untouched.
			dispatch({ type: "setValues", values: settingsToFormValues(settings) });
			toastSettingsResult(settings);
			queryClient.setQueryData(getCloudSettingsQueryKey(), settings);
			await queryClient.invalidateQueries({ queryKey: getCloudSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const isActionPending = saveMutation.isPending || clearMutation.isPending;
	const settings = settingsData;

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

				{settingsIsLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading cloud settings…</Text>
					</Group>
				) : null}

				{settingsError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsError)}
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
								dispatch({ type: "setField", field: "endpoint", value });
							}}
							onBlur={() => dispatch({ type: "touchField", field: "endpoint" })}
							error={visibleErrors.endpoint}
						/>
						<TextInput
							label="Deployment name"
							placeholder="gpt-4o"
							value={formValues.deploymentName}
							onChange={(event) => {
								const value = event.currentTarget.value;
								dispatch({ type: "setField", field: "deploymentName", value });
							}}
							onBlur={() => dispatch({ type: "touchField", field: "deploymentName" })}
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
								dispatch({ type: "setField", field: "apiKey", value });
							}}
							onBlur={() => dispatch({ type: "touchField", field: "apiKey" })}
							error={visibleErrors.apiKey}
						/>
						<Group>
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								onClick={() => {
									dispatch({ type: "submit" });
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
								onClick={() => settingsRefetch()}
								disabled={settingsIsFetching}
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
