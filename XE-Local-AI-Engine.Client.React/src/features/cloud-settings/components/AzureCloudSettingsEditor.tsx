import { Alert, Badge, Button, Group, PasswordInput, SegmentedControl, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconRefresh, IconTrash } from "@tabler/icons-react";
import type { Dispatch } from "react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { AzureCloudSettingsDynamicFields } from "@/features/cloud-settings/components/AzureCloudSettingsDynamicFields";
import { EntraConnectionFields } from "@/features/cloud-settings/entra/components/EntraConnectionFields";
import type { CloudSettingsFormAction } from "@/features/cloud-settings/models/CloudSettingsFormState";
import type {
	CloudApiSurface,
	CloudAuthMode,
	CloudSettingsFormValues,
} from "@/features/cloud-settings/models/CloudSettingsModel";

interface AzureCloudSettingsEditorProps {
	readonly formValues: CloudSettingsFormValues;
	readonly visibleErrors: Partial<Record<keyof CloudSettingsFormValues, string>>;
	readonly modelRowIds: readonly string[];
	readonly headerRowIds: readonly string[];
	readonly hostSuffixRowIds: readonly string[];
	readonly dispatch: Dispatch<CloudSettingsFormAction>;
	readonly connection: {
		readonly isStored: boolean;
		readonly hasApiKey: boolean;
		readonly hasEntraClientSecret: boolean;
	};
	readonly signIn: {
		readonly showDeviceCode: boolean;
		readonly showAuthorizationCode: boolean;
	};
	readonly status: {
		readonly hasErrors: boolean;
		readonly showManagedIdentityEgressWarning: boolean;
		readonly isSaving: boolean;
		readonly isClearing: boolean;
		readonly isActionPending: boolean;
		readonly isReloading: boolean;
	};
	readonly onSave: () => void;
	readonly onClear: () => void;
	readonly onReload: () => void;
}

const segmentedControlStyles = { label: { whiteSpace: "normal" as const } };

export function AzureCloudSettingsEditor(props: AzureCloudSettingsEditorProps) {
	const { t } = useTranslation();
	const { formValues, visibleErrors, modelRowIds, headerRowIds, hostSuffixRowIds, dispatch, connection, signIn, status } = props;
	const isManagedIdentity = formValues.authMode === "ManagedIdentity";
	const isEntraId = formValues.authMode === "EntraId";
	return (
		<SectionCard
			title={t("pages.cloudSettings.azure.title", "Azure OpenAI")}
			actions={
				<Badge color={connection.isStored ? "green" : "gray"}>
					{connection.isStored
						? t("pages.cloudSettings.azure.configured", "Configured")
						: t("pages.cloudSettings.azure.notConfigured", "Not configured")}
				</Badge>
			}
		>
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
					fullWidth={true}
					styles={segmentedControlStyles}
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
					fullWidth={true}
					styles={segmentedControlStyles}
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
					hasStoredClientSecret={connection.hasEntraClientSecret}
					showDeviceCodeSignIn={signIn.showDeviceCode}
					showAuthCodeSignIn={signIn.showAuthorizationCode}
					onFieldChange={(field, value) => dispatch({ type: "setField", field, value })}
					onFieldBlur={(field) => dispatch({ type: "touchField", field })}
					onSignInMethodChange={(value) => dispatch({ type: "setEntraSignInMethod", value })}
				/>
			) : (
				<PasswordInput
					label={t("pages.cloudSettings.azure.apiKeyLabel", "API key")}
					description={
						connection.hasApiKey ? t("pages.cloudSettings.azure.apiKeyStoredHint") : t("pages.cloudSettings.azure.apiKeyHint")
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

			<AzureCloudSettingsDynamicFields
				values={formValues}
				errors={visibleErrors}
				modelRowIds={modelRowIds}
				headerRowIds={headerRowIds}
				hostSuffixRowIds={hostSuffixRowIds}
				dispatch={dispatch}
			/>
			{status.showManagedIdentityEgressWarning ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="cloud-settings-mi-egress-warning">
					<Text size="sm">{t("pages.cloudSettings.azure.managedIdentityEgressWarning")}</Text>
				</Alert>
			) : null}

			<Group>
				<Button
					leftSection={<IconDeviceFloppy size={16} />}
					onClick={props.onSave}
					loading={status.isSaving}
					disabled={status.hasErrors || status.isActionPending}
				>
					{t("pages.cloudSettings.azure.save", "Save cloud settings")}
				</Button>
				<Button
					variant="outline"
					color="red"
					leftSection={<IconTrash size={16} />}
					onClick={() => props.onClear()}
					loading={status.isClearing}
					disabled={!connection.isStored || status.isActionPending}
				>
					{t("pages.cloudSettings.azure.clear", "Clear saved credentials")}
				</Button>
				<Button
					variant="subtle"
					leftSection={<IconRefresh size={16} />}
					onClick={() => props.onReload()}
					disabled={status.isReloading}
				>
					{t("pages.cloudSettings.azure.reload", "Reload")}
				</Button>
			</Group>
		</SectionCard>
	);
}
