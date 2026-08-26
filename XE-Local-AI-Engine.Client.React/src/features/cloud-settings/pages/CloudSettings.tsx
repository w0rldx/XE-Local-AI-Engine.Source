import { Alert, Badge, Card, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCloud } from "@tabler/icons-react";
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
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { toast } from "@/core/ui/notifications/Toast";
import { CodexSignInCard } from "@/features/cloud-settings/codex/components/CodexSignInCard";
import { AzureCloudSettingsEditor } from "@/features/cloud-settings/components/AzureCloudSettingsEditor";
import {
	createFormRowIds,
	errorMessage,
	formReducer,
	initialFormState,
	settingsToFormValues,
	toastSettingsResult,
} from "@/features/cloud-settings/models/CloudSettingsFormState";
import {
	type CloudFoundryModelDraft,
	type CloudHeaderDraft,
	type CloudSettingsFormValues,
	shouldWarnManagedIdentityEgress,
	validateCloudSettingsForm,
} from "@/features/cloud-settings/models/CloudSettingsModel";

function configuredModels(models: readonly CloudFoundryModelDraft[]) {
	return models.reduce<Array<{ deploymentName: string; displayLabel?: string }>>((configured, model) => {
		const deploymentName = model.deploymentName.trim();
		if (deploymentName.length > 0) {
			const displayLabel = model.displayLabel.trim();
			configured.push({ deploymentName, ...(displayLabel.length > 0 ? { displayLabel } : {}) });
		}
		return configured;
	}, []);
}

function configuredHeaders(headers: readonly CloudHeaderDraft[]) {
	return headers.reduce<Array<{ name: string; isSecret: boolean; value?: string }>>((configured, header) => {
		const name = header.name.trim();
		if (name.length > 0) {
			configured.push({
				name,
				isSecret: header.isSecret,
				value: header.isSecret ? (header.value.trim().length > 0 ? header.value : undefined) : header.value,
			});
		}
		return configured;
	}, []);
}

function configuredHostSuffixes(suffixes: readonly string[]) {
	return suffixes.reduce<string[]>((configured, suffix) => {
		const value = suffix.trim();
		if (value.length > 0) {
			configured.push(value);
		}
		return configured;
	}, []);
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
	const { values: formValues, touched: touchedFields, submitted, modelRowIds, headerRowIds, hostSuffixRowIds } = formState;

	// Tracks whether the Codex OAuth session is active (reported by CodexSignInCard).
	// When signed in, Codex is the active chat provider — no CloudSettings save needed.
	const [codexSignedIn, setCodexSignedIn] = useState(false);
	const handleCodexSignedInChange = useCallback((signedIn: boolean) => {
		setCodexSignedIn(signedIn);
	}, []);

	useEffect(() => {
		if (settingsData) {
			const values = settingsToFormValues(settingsData);
			dispatch({ type: "reset", values, rowIds: createFormRowIds(values) });
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
			const values = settingsToFormValues(settings);
			dispatch({ type: "reset", values, rowIds: createFormRowIds(values) });
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
			const values = settingsToFormValues(settings);
			dispatch({ type: "setValues", values, rowIds: createFormRowIds(values) });
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
				models: configuredModels(formValues.models),
				// Drop blank-name rows. Secret rows send no value when left blank so the stored secret is kept;
				// a typed value replaces it. Non-secret values round-trip as entered.
				headers: configuredHeaders(formValues.headers),
				additionalAllowedHostSuffixes: configuredHostSuffixes(formValues.hostSuffixes),
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
		<PageShell>
			<PageHeader
				title={t("pages.cloudSettings.title", "Cloud settings")}
				icon={<IconCloud size={24} />}
				subtitle={t(
					"pages.cloudSettings.subtitle",
					"Store Azure OpenAI credentials locally for cloud-backed runtime mode. Saved API keys are never returned to this page.",
				)}
			/>

			{settingsIsLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.cloudSettings.loading", "Loading cloud settings…")}</Text>
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
				<Card p="md">
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

			<AzureCloudSettingsEditor
				formValues={formValues}
				visibleErrors={visibleErrors}
				modelRowIds={modelRowIds}
				headerRowIds={headerRowIds}
				hostSuffixRowIds={hostSuffixRowIds}
				dispatch={dispatch}
				connection={{
					isStored: hasStoredConnection,
					hasApiKey: azure?.hasStoredApiKey ?? false,
					hasEntraClientSecret: azure?.hasStoredEntraClientSecret ?? false,
				}}
				signIn={{
					showDeviceCode: showEntraDeviceCodeSignIn,
					showAuthorizationCode: showEntraAuthCodeSignIn,
				}}
				status={{
					hasErrors,
					showManagedIdentityEgressWarning,
					isSaving: saveMutation.isPending,
					isClearing: clearMutation.isPending,
					isActionPending,
					isReloading: settingsIsFetching,
				}}
				onSave={handleSave}
				onClear={() => clearMutation.mutate({})}
				onReload={() => settingsRefetch()}
			/>
		</PageShell>
	);
}
