// EntraId connection fields (tenant/client/secret/scope + sign-in method) for the Azure Foundry auth-mode
// section of CloudSettings. Extracted so the page component stays a manageable size — this piece owns only
// the field markup; all form state lives in the parent's reducer and is passed in as callbacks.

import { PasswordInput, SegmentedControl, Stack, Text, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { EntraDeviceCodeSignInCard } from "@/features/cloud-settings/entra/components/EntraDeviceCodeSignInCard";
import {
	type CloudSettingsFormValues,
	type EntraSignInMethod,
	parseEntraSignInMethod,
} from "@/features/cloud-settings/models/CloudSettingsModel";

type EntraTextField = "entraTenantId" | "entraClientId" | "entraClientSecret" | "entraTokenScope";

interface EntraConnectionFieldsProps {
	values: CloudSettingsFormValues;
	errors: Partial<Record<keyof CloudSettingsFormValues, string>>;
	/** True when the backend has a stored client secret for this connection (drives the "stored" hint). */
	hasStoredClientSecret: boolean;
	/** True for a saved EntraId+DeviceCode connection — gates the inline device-code sign-in card. */
	showDeviceCodeSignIn: boolean;
	onFieldChange: (field: EntraTextField, value: string) => void;
	onFieldBlur: (field: EntraTextField) => void;
	onSignInMethodChange: (value: EntraSignInMethod) => void;
}

export function EntraConnectionFields({
	values,
	errors,
	hasStoredClientSecret,
	showDeviceCodeSignIn,
	onFieldChange,
	onFieldBlur,
	onSignInMethodChange,
}: EntraConnectionFieldsProps) {
	const { t } = useTranslation();

	// The sign-in method picker (and the device-code sign-in card) only matter once no client secret will be in
	// play — a typed value or a previously stored one both coerce the connection to ClientSecret mode
	// server-side (mirrors CloudSettingsEndpointDtoMapper.ParseEntraSignInMethod's `hasSecret` check).
	const hasSecret = values.entraClientSecret.trim().length > 0 || hasStoredClientSecret;

	return (
		<Stack gap="sm">
			<Text size="sm" c="dimmed">
				{t(
					"pages.cloudSettings.entra.modeHint",
					"Entra ID requests its own bearer token — app-only client-credentials when a secret is configured, otherwise interactive user sign-in — and sends it as the Authorization header. Intended for gateways (for example an Azure APIM AI gateway) that validate an Entra ID token.",
				)}
			</Text>
			<TextInput
				label={t("pages.cloudSettings.entra.tenantIdLabel", "Tenant ID")}
				placeholder={t("pages.cloudSettings.entra.tenantIdPlaceholder", "00000000-0000-0000-0000-000000000000")}
				value={values.entraTenantId}
				onChange={(event) => onFieldChange("entraTenantId", event.currentTarget.value)}
				onBlur={() => onFieldBlur("entraTenantId")}
				error={errors.entraTenantId}
			/>
			<TextInput
				label={t("pages.cloudSettings.entra.clientIdLabel", "Client ID")}
				placeholder={t("pages.cloudSettings.entra.clientIdPlaceholder", "00000000-0000-0000-0000-000000000000")}
				value={values.entraClientId}
				onChange={(event) => onFieldChange("entraClientId", event.currentTarget.value)}
				onBlur={() => onFieldBlur("entraClientId")}
				error={errors.entraClientId}
			/>
			<PasswordInput
				label={t("pages.cloudSettings.entra.clientSecretLabel", "Client secret")}
				description={
					hasStoredClientSecret
						? t("pages.cloudSettings.entra.clientSecretStoredHint", "A secret is stored. Enter a new value to replace it, or leave blank to keep it.")
						: t("pages.cloudSettings.entra.clientSecretHint", "Leave blank to sign in interactively instead of using app-only client-credentials.")
				}
				value={values.entraClientSecret}
				onChange={(event) => onFieldChange("entraClientSecret", event.currentTarget.value)}
				onBlur={() => onFieldBlur("entraClientSecret")}
			/>
			<TextInput
				label={t("pages.cloudSettings.entra.tokenScopeLabel", "Token scope")}
				placeholder={t("pages.cloudSettings.entra.tokenScopePlaceholder", "api://<backend-app-id>/.default")}
				description={
					hasSecret
						? t(
								"pages.cloudSettings.entra.tokenScopeHintClientCredentials",
								"With a client secret, use the app-only scope ending in /.default, e.g. api://<app-id-uri>/.default.",
							)
						: t(
								"pages.cloudSettings.entra.tokenScopeHintDelegated",
								"Without a client secret (device-code or browser sign-in), use a delegated scope, e.g. api://<app-id-uri>/access_as_user.",
							)
				}
				value={values.entraTokenScope}
				onChange={(event) => onFieldChange("entraTokenScope", event.currentTarget.value)}
				onBlur={() => onFieldBlur("entraTokenScope")}
				error={errors.entraTokenScope}
			/>
			{!hasSecret ? (
				<Stack gap={4}>
					<Text size="sm" fw={500}>
						{t("pages.cloudSettings.entra.signInMethodLabel", "Sign-in method")}
					</Text>
					<SegmentedControl
						data-testid="cloud-settings-entra-sign-in-method"
						value={values.entraSignInMethod}
						onChange={(value) => onSignInMethodChange(parseEntraSignInMethod(value))}
						data={[
							{ value: "DeviceCode", label: t("pages.cloudSettings.entra.signInMethodDeviceCode", "Device code") },
							{
								value: "InteractiveBrowser",
								label: t("pages.cloudSettings.entra.signInMethodInteractiveBrowser", "Interactive browser"),
							},
						]}
					/>
				</Stack>
			) : null}
			{showDeviceCodeSignIn ? <EntraDeviceCodeSignInCard /> : null}
		</Stack>
	);
}
