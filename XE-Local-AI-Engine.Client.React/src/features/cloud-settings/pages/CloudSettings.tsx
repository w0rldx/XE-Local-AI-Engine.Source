import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Card,
	Container,
	Group,
	Loader,
	PasswordInput,
	SegmentedControl,
	Stack,
	Switch,
	Text,
	TextInput,
	Title,
} from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconPlus, IconRefresh, IconTrash } from "@tabler/icons-react";
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
import { EntraConnectionFields } from "@/features/cloud-settings/entra/components/EntraConnectionFields";
import {
	type CloudApiSurface,
	type CloudAuthMode,
	type CloudFoundryModelDraft,
	type CloudSettingsFormValues,
	type EntraSignInMethod,
	parseApiSurface,
	parseEntraSignInMethod,
	shouldWarnManagedIdentityEgress,
	validateCloudSettingsForm,
} from "@/features/cloud-settings/models/CloudSettingsModel";

// The generated cloud-settings responses share one shape, so both the save and clear mutations resolve the same
// view; this alias keeps the onSuccess handlers readable.
type CloudSettings = SaveCloudSettingsResponse;

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected cloud settings error";
}

// Always show at least one (blank) deployment row so the user has somewhere to type on a fresh connection.
function withAtLeastOneRow(models: CloudFoundryModelDraft[]): CloudFoundryModelDraft[] {
	return models.length > 0 ? models : [{ deploymentName: "", displayLabel: "" }];
}

// Save and clear return the same view; both reset the form to the stored (redacted) values. The API key is never
// echoed back, so it always resets to empty. Module-scoped because it uses no component state.
function settingsToFormValues(settings: CloudSettings): CloudSettingsFormValues {
	const azure = settings.azureFoundry;
	return {
		endpoint: azure?.endpoint ?? "",
		authMode: (azure?.authMode as CloudAuthMode) ?? "ApiKey",
		apiSurface: parseApiSurface(azure?.apiSurface),
		apiKey: "",
		models: withAtLeastOneRow(
			(azure?.models ?? []).map((model) => ({
				deploymentName: model.deploymentName ?? "",
				displayLabel: model.displayLabel ?? "",
			})),
		),
		// Secret header values are write-only: they load blank with a "stored" hint driven by hasStoredValue;
		// non-secret values round-trip for inline editing.
		headers: (azure?.headers ?? []).map((header) => ({
			name: header.name ?? "",
			value: header.isSecret ? "" : (header.value ?? ""),
			isSecret: header.isSecret ?? false,
			hasStoredValue: header.hasStoredValue ?? false,
		})),
		hostSuffixes: azure?.additionalAllowedHostSuffixes ?? [],
		entraTenantId: azure?.entraTenantId ?? "",
		entraClientId: azure?.entraClientId ?? "",
		// Write-only, like apiKey: always loads blank; a "stored" hint is driven by hasStoredEntraClientSecret.
		entraClientSecret: "",
		entraTokenScope: azure?.entraTokenScope ?? "",
		entraSignInMethod: parseEntraSignInMethod(azure?.entraSignInMethod),
		entraAuthCodeRedirectUri: azure?.entraAuthCodeRedirectUri ?? "",
	};
}

function toastSettingsResult(settings: CloudSettings): void {
	toast.success(
		settings.azureFoundry
			? "Cloud settings saved. Capability reporting was requested."
			: "Cloud settings cleared.",
	);
}

const emptyFormValues: CloudSettingsFormValues = {
	endpoint: "",
	authMode: "ApiKey",
	apiSurface: "AzureDeployments",
	apiKey: "",
	models: [{ deploymentName: "", displayLabel: "" }],
	headers: [],
	hostSuffixes: [],
	entraTenantId: "",
	entraClientId: "",
	entraClientSecret: "",
	entraTokenScope: "",
	entraSignInMethod: "DeviceCode",
	entraAuthCodeRedirectUri: "",
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
	| {
			type: "setField";
			field:
				| "endpoint"
				| "apiKey"
				| "entraTenantId"
				| "entraClientId"
				| "entraClientSecret"
				| "entraTokenScope"
				| "entraAuthCodeRedirectUri";
			value: string;
	  }
	| { type: "setAuthMode"; value: CloudAuthMode }
	| { type: "setApiSurface"; value: CloudApiSurface }
	| { type: "setEntraSignInMethod"; value: EntraSignInMethod }
	| { type: "addModel" }
	| { type: "removeModel"; index: number }
	| { type: "setModelField"; index: number; field: keyof CloudFoundryModelDraft; value: string }
	| { type: "addHeader" }
	| { type: "removeHeader"; index: number }
	| { type: "setHeaderField"; index: number; field: "name" | "value"; value: string }
	| { type: "toggleHeaderSecret"; index: number }
	| { type: "addHostSuffix" }
	| { type: "removeHostSuffix"; index: number }
	| { type: "setHostSuffix"; index: number; value: string }
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
		case "setAuthMode":
			return { ...state, values: { ...state.values, authMode: action.value } };
		case "setApiSurface":
			return { ...state, values: { ...state.values, apiSurface: action.value } };
		case "setEntraSignInMethod":
			return { ...state, values: { ...state.values, entraSignInMethod: action.value } };
		case "addModel":
			return {
				...state,
				values: { ...state.values, models: [...state.values.models, { deploymentName: "", displayLabel: "" }] },
			};
		case "removeModel": {
			// Never drop the last row — keep one blank row so the list is always editable.
			const next = state.values.models.filter((_, index) => index !== action.index);
			return { ...state, values: { ...state.values, models: withAtLeastOneRow(next) } };
		}
		case "setModelField": {
			const next = state.values.models.map((model, index) =>
				index === action.index ? { ...model, [action.field]: action.value } : model,
			);
			return { ...state, values: { ...state.values, models: next } };
		}
		case "addHeader":
			return {
				...state,
				values: {
					...state.values,
					headers: [...state.values.headers, { name: "", value: "", isSecret: false, hasStoredValue: false }],
				},
			};
		case "removeHeader":
			return {
				...state,
				values: { ...state.values, headers: state.values.headers.filter((_, index) => index !== action.index) },
			};
		case "setHeaderField": {
			const next = state.values.headers.map((header, index) =>
				index === action.index ? { ...header, [action.field]: action.value } : header,
			);
			return { ...state, values: { ...state.values, headers: next } };
		}
		case "toggleHeaderSecret": {
			const next = state.values.headers.map((header, index) =>
				index === action.index ? { ...header, isSecret: !header.isSecret } : header,
			);
			return { ...state, values: { ...state.values, headers: next } };
		}
		case "addHostSuffix":
			return { ...state, values: { ...state.values, hostSuffixes: [...state.values.hostSuffixes, ""] } };
		case "removeHostSuffix":
			return {
				...state,
				values: {
					...state.values,
					hostSuffixes: state.values.hostSuffixes.filter((_, index) => index !== action.index),
				},
			};
		case "setHostSuffix": {
			const next = state.values.hostSuffixes.map((suffix, index) => (index === action.index ? action.value : suffix));
			return { ...state, values: { ...state.values, hostSuffixes: next } };
		}
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
	const {
		data: settingsData,
		isLoading: settingsIsLoading,
		error: settingsError,
		refetch: settingsRefetch,
		isFetching: settingsIsFetching,
	} = useQuery(withResponseValidation(getCloudSettingsOptions()));
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
			dispatch({ type: "reset", values: settingsToFormValues(settingsData) });
		}
	}, [settingsData]);

	// Write-only field: a stored secret must still be validated against the client-credentials scope requirement
	// even when the form's `entraClientSecret` is blank (blank keeps the stored value server-side).
	const hasStoredEntraClientSecret = settingsData?.azureFoundry?.hasStoredEntraClientSecret ?? false;
	const errors = useMemo(
		() => validateCloudSettingsForm(formValues, hasStoredEntraClientSecret),
		[formValues, hasStoredEntraClientSecret],
	);
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
	const azure = settings?.azureFoundry;
	// A connection is stored when any of its fields persisted — gates the Clear button (managed identity stores
	// no key, so hasStoredApiKey alone is insufficient).
	const hasStoredConnection = Boolean(azure?.endpoint) || (azure?.hasStoredApiKey ?? false) || (azure?.models?.length ?? 0) > 0;
	const isManagedIdentity = formValues.authMode === "ManagedIdentity";
	const isEntraId = formValues.authMode === "EntraId";
	const showEntraDeviceCodeSignIn =
		azure?.authMode === "EntraId" &&
		azure.entraSignInMethod === "DeviceCode" &&
		Boolean(azure.entraTenantId) &&
		Boolean(azure.entraClientId);
	const showEntraAuthCodeSignIn =
		azure?.authMode === "EntraId" &&
		azure.entraSignInMethod === "AuthorizationCode" &&
		Boolean(azure.entraTenantId) &&
		Boolean(azure.entraClientId) &&
		Boolean(azure.hasStoredEntraClientSecret);
	const showManagedIdentityEgressWarning = useMemo(() => shouldWarnManagedIdentityEgress(formValues), [formValues]);

	const handleSave = (): void => {
		dispatch({ type: "submit" });
		// Always dispatch submit so inline errors render, but only fire the mutation when the form is valid —
		// mirrors the disabled state on the Save button as defense in depth (e.g. non-pointer submit paths).
		if (hasErrors) {
			return;
		}
		saveMutation.mutate({
			body: {
				providerName: "AzureFoundry",
				endpoint: formValues.endpoint.trim(),
				authMode: formValues.authMode,
				apiSurface: formValues.apiSurface,
				apiKey: isManagedIdentity ? undefined : formValues.apiKey.trim(),
				models: formValues.models
					.map((model) => ({
						deploymentName: model.deploymentName.trim(),
						displayLabel: model.displayLabel.trim().length > 0 ? model.displayLabel.trim() : undefined,
					}))
					.filter((model) => model.deploymentName.length > 0),
				// Drop blank-name rows. Secret rows send no value when left blank so the stored secret is kept;
				// a typed value replaces it. Non-secret values round-trip as entered.
				headers: formValues.headers
					.filter((header) => header.name.trim().length > 0)
					.map((header) => ({
						name: header.name.trim(),
						isSecret: header.isSecret,
						value: header.isSecret ? (header.value.trim().length > 0 ? header.value : undefined) : header.value,
					})),
				additionalAllowedHostSuffixes: formValues.hostSuffixes
					.map((suffix) => suffix.trim())
					.filter((suffix) => suffix.length > 0),
				// Ignored by the backend outside EntraId mode. A blank secret keeps the stored secret (or selects
				// interactive sign-in when none is stored) — same write-only semantics as a secret header value.
				entraTenantId: formValues.entraTenantId.trim(),
				entraClientId: formValues.entraClientId.trim(),
				entraClientSecret: formValues.entraClientSecret.trim().length > 0 ? formValues.entraClientSecret : undefined,
				entraTokenScope: formValues.entraTokenScope.trim(),
				entraSignInMethod: formValues.entraSignInMethod,
				entraAuthCodeRedirectUri:
					formValues.entraAuthCodeRedirectUri.trim().length > 0 ? formValues.entraAuthCodeRedirectUri.trim() : undefined,
			},
		});
	};

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						{t("common.workerNode", "Worker Node")}
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
							) : hasStoredConnection ? (
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
							<Title order={3}>{t("pages.cloudSettings.azure.title", "Azure OpenAI")}</Title>
							<Badge color={hasStoredConnection ? "green" : "gray"}>
								{hasStoredConnection
									? t("pages.cloudSettings.azure.configured", "Configured")
									: t("pages.cloudSettings.azure.notConfigured", "Not configured")}
							</Badge>
						</Group>
						<Text c="dimmed">{t("pages.cloudSettings.azure.description")}</Text>
						<TextInput
							label={t("pages.cloudSettings.azure.endpointLabel", "Azure OpenAI endpoint")}
							placeholder={t("pages.cloudSettings.azure.endpointPlaceholder", "https://example.openai.azure.com/")}
							value={formValues.endpoint}
							onChange={(event) => {
								const value = event.currentTarget.value;
								dispatch({ type: "setField", field: "endpoint", value });
							}}
							onBlur={() => dispatch({ type: "touchField", field: "endpoint" })}
							error={visibleErrors.endpoint}
						/>

						<Stack gap={4}>
							<Text size="sm" fw={500}>
								{t("pages.cloudSettings.azure.authModeLabel", "Authentication")}
							</Text>
							<SegmentedControl
								data-testid="cloud-settings-auth-mode"
								value={formValues.authMode}
								onChange={(value) => dispatch({ type: "setAuthMode", value: value as CloudAuthMode })}
								data={[
									{ value: "ApiKey", label: t("pages.cloudSettings.azure.authModeApiKey", "API key") },
									{
										value: "ManagedIdentity",
										label: t("pages.cloudSettings.azure.authModeManagedIdentity", "Managed identity"),
									},
										{ value: "EntraId", label: t("pages.cloudSettings.azure.authModeEntraId", "Entra ID") },
								]}
							/>
						</Stack>

						<Stack gap={4}>
							<Text size="sm" fw={500}>
								{t("pages.cloudSettings.azure.apiSurfaceLabel", "API surface")}
							</Text>
							<SegmentedControl
								data-testid="cloud-settings-api-surface"
								value={formValues.apiSurface}
								onChange={(value) => dispatch({ type: "setApiSurface", value: value as CloudApiSurface })}
								data={[
									{
										value: "AzureDeployments",
										label: t("pages.cloudSettings.azure.apiSurfaceAzureDeployments", "Azure deployments (default)"),
									},
									{
										value: "OpenAiV1",
										label: t("pages.cloudSettings.azure.apiSurfaceOpenAiV1", "OpenAI v1 (Foundry / gateway)"),
									},
								]}
							/>
							<Text size="xs" c="dimmed">
								{t("pages.cloudSettings.azure.apiSurfaceHint")}
							</Text>
						</Stack>

						{isManagedIdentity ? (
							<Text size="sm" c="dimmed">
								{t("pages.cloudSettings.azure.managedIdentityHint")}
							</Text>
						) : isEntraId ? (
							<EntraConnectionFields
								values={formValues}
								errors={visibleErrors}
								hasStoredClientSecret={azure?.hasStoredEntraClientSecret ?? false}
								showDeviceCodeSignIn={showEntraDeviceCodeSignIn}
								showAuthCodeSignIn={showEntraAuthCodeSignIn}
								onFieldChange={(field, value) => dispatch({ type: "setField", field, value })}
								onFieldBlur={(field) => dispatch({ type: "touchField", field })}
								onSignInMethodChange={(value) => dispatch({ type: "setEntraSignInMethod", value })}
							/>
						) : (
							<PasswordInput
								label={t("pages.cloudSettings.azure.apiKeyLabel", "API key")}
								description={
									azure?.hasStoredApiKey
										? t("pages.cloudSettings.azure.apiKeyStoredHint")
										: t("pages.cloudSettings.azure.apiKeyHint")
								}
								value={formValues.apiKey}
								onChange={(event) => {
									const value = event.currentTarget.value;
									dispatch({ type: "setField", field: "apiKey", value });
								}}
								onBlur={() => dispatch({ type: "touchField", field: "apiKey" })}
								error={visibleErrors.apiKey}
							/>
						)}

						<Stack gap={6}>
							<Text size="sm" fw={500}>
								{t("pages.cloudSettings.azure.modelsLabel", "Models")}
							</Text>
							<Text size="xs" c="dimmed">
								{t("pages.cloudSettings.azure.deploymentNameHelp")}
							</Text>
							{formValues.models.map((model, index) => (
								// biome-ignore lint/suspicious/noArrayIndexKey: rows are positional and have no stable id; index is the row identity.
								<Group key={index} align="flex-end" gap="xs" wrap="nowrap">
									<TextInput
										style={{ flex: 1 }}
										aria-label={t("pages.cloudSettings.azure.deploymentNameLabel", "Deployment name")}
										label={index === 0 ? t("pages.cloudSettings.azure.deploymentNameLabel", "Deployment name") : undefined}
										placeholder={t("pages.cloudSettings.azure.deploymentNamePlaceholder", "gpt-4o")}
										value={model.deploymentName}
										onChange={(event) => {
											const value = event.currentTarget.value;
											dispatch({ type: "setModelField", index, field: "deploymentName", value });
										}}
										onBlur={() => dispatch({ type: "touchField", field: "models" })}
									/>
									<TextInput
										style={{ flex: 1 }}
										aria-label={t("pages.cloudSettings.azure.displayLabelLabel", "Display label (optional)")}
										label={index === 0 ? t("pages.cloudSettings.azure.displayLabelLabel", "Display label (optional)") : undefined}
										placeholder={t("pages.cloudSettings.azure.displayLabelPlaceholder", "GPT-4o")}
										value={model.displayLabel}
										onChange={(event) => {
											const value = event.currentTarget.value;
											dispatch({ type: "setModelField", index, field: "displayLabel", value });
										}}
									/>
									<ActionIcon
										variant="subtle"
										color="red"
										size="lg"
										data-testid={`cloud-settings-remove-model-${index}`}
										aria-label={t("pages.cloudSettings.azure.removeModel", "Remove model")}
										onClick={() => dispatch({ type: "removeModel", index })}
									>
										<IconTrash size={16} />
									</ActionIcon>
								</Group>
							))}
							{visibleErrors.models ? (
								<Text size="xs" c="red" data-testid="cloud-settings-models-error">
									{visibleErrors.models}
								</Text>
							) : null}
							<Group>
								<Button
									variant="light"
									size="xs"
									leftSection={<IconPlus size={14} />}
									data-testid="cloud-settings-add-model"
									onClick={() => dispatch({ type: "addModel" })}
								>
									{t("pages.cloudSettings.azure.addModel", "Add model")}
								</Button>
							</Group>
						</Stack>

						<Stack gap={6}>
							<Text size="sm" fw={500}>
								{t("pages.cloudSettings.azure.headers.title", "Custom headers")}
							</Text>
							<Text size="xs" c="dimmed">
								{t("pages.cloudSettings.azure.headers.description")}
							</Text>
							{formValues.headers.map((header, index) => (
								// biome-ignore lint/suspicious/noArrayIndexKey: rows are positional and have no stable id; index is the row identity.
								<Group key={index} align="flex-end" gap="xs" wrap="nowrap">
									<TextInput
										style={{ flex: 1 }}
										aria-label={t("pages.cloudSettings.azure.headers.nameLabel", "Header name")}
										label={index === 0 ? t("pages.cloudSettings.azure.headers.nameLabel", "Header name") : undefined}
										placeholder={t("pages.cloudSettings.azure.headers.namePlaceholder", "Ocp-Apim-Subscription-Key")}
										value={header.name}
										onChange={(event) => {
											const value = event.currentTarget.value;
											dispatch({ type: "setHeaderField", index, field: "name", value });
										}}
										onBlur={() => dispatch({ type: "touchField", field: "headers" })}
									/>
									{header.isSecret ? (
										<PasswordInput
											style={{ flex: 1 }}
											aria-label={t("pages.cloudSettings.azure.headers.valueLabel", "Value")}
											label={index === 0 ? t("pages.cloudSettings.azure.headers.valueLabel", "Value") : undefined}
											description={header.hasStoredValue ? t("pages.cloudSettings.azure.headers.secretStoredHint") : undefined}
											placeholder={t("pages.cloudSettings.azure.headers.valuePlaceholder", "value")}
											value={header.value}
											onChange={(event) => {
												const value = event.currentTarget.value;
												dispatch({ type: "setHeaderField", index, field: "value", value });
											}}
											onBlur={() => dispatch({ type: "touchField", field: "headers" })}
										/>
									) : (
										<TextInput
											style={{ flex: 1 }}
											aria-label={t("pages.cloudSettings.azure.headers.valueLabel", "Value")}
											label={index === 0 ? t("pages.cloudSettings.azure.headers.valueLabel", "Value") : undefined}
											placeholder={t("pages.cloudSettings.azure.headers.valuePlaceholder", "value")}
											value={header.value}
											onChange={(event) => {
												const value = event.currentTarget.value;
												dispatch({ type: "setHeaderField", index, field: "value", value });
											}}
											onBlur={() => dispatch({ type: "touchField", field: "headers" })}
										/>
									)}
									<Switch
										data-testid={`cloud-settings-header-secret-${index}`}
										aria-label={t("pages.cloudSettings.azure.headers.secretLabel", "Secret")}
										label={index === 0 ? t("pages.cloudSettings.azure.headers.secretLabel", "Secret") : undefined}
										checked={header.isSecret}
										onChange={() => dispatch({ type: "toggleHeaderSecret", index })}
									/>
									<ActionIcon
										variant="subtle"
										color="red"
										size="lg"
										data-testid={`cloud-settings-remove-header-${index}`}
										aria-label={t("pages.cloudSettings.azure.headers.removeHeader", "Remove header")}
										onClick={() => dispatch({ type: "removeHeader", index })}
									>
										<IconTrash size={16} />
									</ActionIcon>
								</Group>
							))}
							{visibleErrors.headers ? (
								<Text size="xs" c="red" data-testid="cloud-settings-headers-error">
									{visibleErrors.headers}
								</Text>
							) : null}
							<Group>
								<Button
									variant="light"
									size="xs"
									leftSection={<IconPlus size={14} />}
									data-testid="cloud-settings-add-header"
									onClick={() => dispatch({ type: "addHeader" })}
								>
									{t("pages.cloudSettings.azure.headers.addHeader", "Add header")}
								</Button>
							</Group>
						</Stack>

						<Stack gap={6}>
							<Text size="sm" fw={500}>
								{t("pages.cloudSettings.azure.hostSuffixes.title", "Allowed host suffixes")}
							</Text>
							<Text size="xs" c="dimmed">
								{t("pages.cloudSettings.azure.hostSuffixes.description")}
							</Text>
							{formValues.hostSuffixes.map((suffix, index) => (
								// biome-ignore lint/suspicious/noArrayIndexKey: rows are positional and have no stable id; index is the row identity.
								<Group key={index} align="flex-end" gap="xs" wrap="nowrap">
									<TextInput
										style={{ flex: 1 }}
										aria-label={t("pages.cloudSettings.azure.hostSuffixes.label", "Host suffix")}
										label={index === 0 ? t("pages.cloudSettings.azure.hostSuffixes.label", "Host suffix") : undefined}
										placeholder={t("pages.cloudSettings.azure.hostSuffixes.placeholder", ".azure-api.net")}
										value={suffix}
										onChange={(event) => {
											const value = event.currentTarget.value;
											dispatch({ type: "setHostSuffix", index, value });
										}}
										onBlur={() => dispatch({ type: "touchField", field: "hostSuffixes" })}
									/>
									<ActionIcon
										variant="subtle"
										color="red"
										size="lg"
										data-testid={`cloud-settings-remove-host-${index}`}
										aria-label={t("pages.cloudSettings.azure.hostSuffixes.removeHost", "Remove allowed host")}
										onClick={() => dispatch({ type: "removeHostSuffix", index })}
									>
										<IconTrash size={16} />
									</ActionIcon>
								</Group>
							))}
							{visibleErrors.hostSuffixes ? (
								<Text size="xs" c="red" data-testid="cloud-settings-host-suffixes-error">
									{visibleErrors.hostSuffixes}
								</Text>
							) : null}
							<Group>
								<Button
									variant="light"
									size="xs"
									leftSection={<IconPlus size={14} />}
									data-testid="cloud-settings-add-host"
									onClick={() => dispatch({ type: "addHostSuffix" })}
								>
									{t("pages.cloudSettings.azure.hostSuffixes.addHost", "Add allowed host")}
								</Button>
							</Group>
						</Stack>

						{showManagedIdentityEgressWarning ? (
							<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="cloud-settings-mi-egress-warning">
								<Text size="sm">{t("pages.cloudSettings.azure.managedIdentityEgressWarning")}</Text>
							</Alert>
						) : null}

						<Group>
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								onClick={handleSave}
								loading={saveMutation.isPending}
								disabled={hasErrors || isActionPending}
							>
								{t("pages.cloudSettings.azure.save", "Save cloud settings")}
							</Button>
							<Button
								variant="outline"
								color="red"
								leftSection={<IconTrash size={16} />}
								onClick={() => clearMutation.mutate({})}
								loading={clearMutation.isPending}
								disabled={!hasStoredConnection || isActionPending}
							>
								{t("pages.cloudSettings.azure.clear", "Clear saved credentials")}
							</Button>
							<Button
								variant="subtle"
								leftSection={<IconRefresh size={16} />}
								onClick={() => settingsRefetch()}
								disabled={settingsIsFetching}
							>
								{t("pages.cloudSettings.azure.reload", "Reload")}
							</Button>
						</Group>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
