import { Group, Loader, Stack, Text } from "@mantine/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useReducer } from "react";
import { useTranslation } from "react-i18next";

import type { ClearCloudSettingsResponse, SaveCloudSettingsResponse } from "@/core/api/generated";
import {
	clearCloudSettingsMutation,
	getCloudSettingsOptions,
	getCloudSettingsQueryKey,
	saveCloudSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { toHeaderRequests } from "@/core/http-headers/CustomHeaders";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { AzureCloudSettingsEditor } from "@/features/cloud-providers/azure/components/AzureCloudSettingsEditor";
import {
	createFormRowIds,
	errorMessage,
	formReducer,
	initialFormState,
	settingsToFormValues,
	toastSettingsResult,
} from "@/features/cloud-providers/azure/models/CloudSettingsFormState";
import {
	type CloudFoundryModelDraft,
	type CloudSettingsFormValues,
	contextLengthToNumber,
	hasStoredAzureConnection,
	shouldWarnManagedIdentityEgress,
	validateCloudSettingsForm,
} from "@/features/cloud-providers/azure/models/CloudSettingsModel";

function configuredModels(models: readonly CloudFoundryModelDraft[]) {
	return models.reduce<Array<{ deploymentName: string; displayLabel?: string; contextLength?: number }>>((configured, model) => {
		const deploymentName = model.deploymentName.trim();
		if (deploymentName.length > 0) {
			const displayLabel = model.displayLabel.trim();
			// Omitted like a blank label: the backend reads a missing window as null (unknown).
			const contextLength = contextLengthToNumber(model.contextLength);
			configured.push({
				deploymentName,
				...(displayLabel.length > 0 ? { displayLabel } : {}),
				...(contextLength !== null ? { contextLength } : {}),
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

// The Azure AI Foundry slot: one connection per node, edited in place, with its Entra sign-in cards.
export function AzureFoundryPanel() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	const {
		data: settingsData,
		isLoading: settingsIsLoading,
		error: settingsError,
		refetch: settingsRefetch,
		isFetching: settingsIsFetching,
	} = useQuery(getCloudSettingsOptions());
	const [formState, dispatch] = useReducer(formReducer, initialFormState);
	const { values: formValues, touched: touchedFields, submitted, modelRowIds, headerRowIds, hostSuffixRowIds } = formState;

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
		...saveCloudSettingsMutation(),
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
		...clearCloudSettingsMutation(),
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

	const handleClear = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.cloudSettings.clearConfirm.title", "Clear the cloud connection?"),
			description: t(
				"pages.cloudSettings.clearConfirm.description",
				"The stored Azure AI Foundry connection and its credentials are deleted. Its cloud models stop working until you enter the connection again. This cannot be undone.",
			),
			confirmationText: t("pages.cloudSettings.clearConfirm.confirm", "Clear"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (confirmed) {
			clearMutation.mutate({});
		}
	};

	const isActionPending = saveMutation.isPending || clearMutation.isPending;
	const settings = settingsData;
	const azure = settings?.azureFoundry;
	// Gates the Clear button.
	const hasStoredConnection = hasStoredAzureConnection(azure);
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
				headers: toHeaderRequests(formValues.headers),
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
		<Stack gap="md" data-testid="azure-foundry-panel">
			{settingsIsLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.cloudSettings.loading", "Loading cloud settings…")}</Text>
				</Group>
			) : null}

			{settingsError ? <InlineErrorAlert message={errorMessage(settingsError)} /> : null}

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
				onClear={handleClear}
				onReload={() => settingsRefetch()}
			/>
		</Stack>
	);
}
