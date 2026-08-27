import { Alert, Badge, Button, Group, PasswordInput, SegmentedControl, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconArrowBackUp, IconDeviceFloppy, IconTrash, IconX } from "@tabler/icons-react";
import type { Dispatch } from "react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { ExternalProviderModelFields } from "@/features/external-providers/components/ExternalProviderModelFields";
import { ExternalProviderProbePanel } from "@/features/external-providers/components/ExternalProviderProbePanel";
import type { ExternalProviderFormAction } from "@/features/external-providers/models/ExternalProviderFormState";
import type {
	ExternalProviderFormErrors,
	ExternalProviderFormValues,
	ExternalProviderLocality,
} from "@/features/external-providers/models/ExternalProviderModel";

interface ExternalProviderConnectionEditorProps {
	readonly values: ExternalProviderFormValues;
	readonly visibleErrors: ExternalProviderFormErrors;
	readonly modelRowIds: readonly string[];
	readonly dispatch: Dispatch<ExternalProviderFormAction>;
	readonly connection: {
		readonly isNew: boolean;
		readonly hasApiKey: boolean;
	};
	readonly status: {
		readonly hasErrors: boolean;
		readonly warnLocalDeclaration: boolean;
		readonly isSaving: boolean;
		readonly isDeleting: boolean;
	};
	readonly onSave: () => void;
	readonly onDelete: () => void;
	readonly onCancel: () => void;
}

const segmentedControlStyles = { label: { whiteSpace: "normal" as const } };

export function ExternalProviderConnectionEditor(props: ExternalProviderConnectionEditorProps) {
	const { t } = useTranslation();
	const { values, visibleErrors, modelRowIds, dispatch, connection, status } = props;
	const isActionPending = status.isSaving || status.isDeleting;

	return (
		<SectionCard
			title={
				connection.isNew
					? t("pages.externalProviders.editor.newTitle", "New connection")
					: t("pages.externalProviders.editor.editTitle", "Edit connection")
			}
			actions={
				<Badge color={values.locality === "Local" ? "teal" : "orange"} variant="light">
					{values.locality === "Local"
						? t("pages.externalProviders.locality.localBadge", "Declared local")
						: t("pages.externalProviders.locality.cloudBadge", "Declared cloud")}
				</Badge>
			}
			data-testid="external-provider-editor"
		>
			<TextInput
				label={t("pages.externalProviders.editor.idLabel", "Connection id")}
				description={
					connection.isNew ? t("pages.externalProviders.editor.idHint") : t("pages.externalProviders.editor.idImmutableHint")
				}
				placeholder={t("pages.externalProviders.editor.idPlaceholder", "unsloth-box")}
				value={values.connectionId}
				disabled={!connection.isNew}
				data-testid="external-provider-id"
				onChange={(event) => {
					const value = event.currentTarget.value;
					dispatch({ type: "setField", field: "connectionId", value });
				}}
				onBlur={() => dispatch({ type: "touchField", field: "connectionId" })}
				error={visibleErrors.connectionId}
			/>

			<TextInput
				label={t("pages.externalProviders.editor.displayNameLabel", "Display name")}
				placeholder={t("pages.externalProviders.editor.displayNamePlaceholder", "Unsloth box")}
				value={values.displayName}
				data-testid="external-provider-display-name"
				onChange={(event) => {
					const value = event.currentTarget.value;
					dispatch({ type: "setField", field: "displayName", value });
				}}
				onBlur={() => dispatch({ type: "touchField", field: "displayName" })}
				error={visibleErrors.displayName}
			/>

			<TextInput
				label={t("pages.externalProviders.editor.baseUrlLabel", "Base URL")}
				description={t("pages.externalProviders.editor.baseUrlHint")}
				placeholder={t("pages.externalProviders.editor.baseUrlPlaceholder", "http://127.0.0.1:8080/v1")}
				value={values.baseUrl}
				data-testid="external-provider-base-url"
				onChange={(event) => {
					const value = event.currentTarget.value;
					dispatch({ type: "setField", field: "baseUrl", value });
				}}
				onBlur={() => dispatch({ type: "touchField", field: "baseUrl" })}
				error={visibleErrors.baseUrl}
			/>

			<Stack gap={4}>
				<Text size="sm" fw={500}>
					{t("pages.externalProviders.locality.label", "Trust")}
				</Text>
				<SegmentedControl
					fullWidth={true}
					styles={segmentedControlStyles}
					data-testid="external-provider-locality"
					value={values.locality}
					onChange={(value) => dispatch({ type: "setLocality", value: value as ExternalProviderLocality })}
					data={[
						{ value: "Local", label: t("pages.externalProviders.locality.local", "Local (self-hosted)") },
						{ value: "Cloud", label: t("pages.externalProviders.locality.cloud", "Cloud (hosted)") },
					]}
				/>
				{/* The declaration is what the trust gates read; the node never verifies it. Both branches say what the
				    operator is actually choosing. */}
				<Text size="xs" c="dimmed">
					{values.locality === "Local"
						? t("pages.externalProviders.locality.localHint")
						: t("pages.externalProviders.locality.cloudHint")}
				</Text>
			</Stack>

			{status.warnLocalDeclaration ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="external-provider-locality-warning">
					<Text size="sm">{t("pages.externalProviders.locality.nonLocalHostWarning")}</Text>
				</Alert>
			) : null}

			{/* A stored key is never returned, so the field loads blank and blank means "keep it". Removal is its own
			    explicit action because there is no value the operator could type to mean "none". */}
			{values.clearApiKey ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="external-provider-key-removal">
					<Group justify="space-between" align="center" wrap="nowrap">
						<Text size="sm">{t("pages.externalProviders.editor.apiKeyRemovalPending")}</Text>
						<Button
							variant="subtle"
							size="xs"
							leftSection={<IconArrowBackUp size={14} />}
							data-testid="external-provider-keep-key"
							onClick={() => dispatch({ type: "keepApiKey" })}
						>
							{t("pages.externalProviders.editor.keepApiKey", "Keep stored key")}
						</Button>
					</Group>
				</Alert>
			) : (
				<Group align="flex-end" gap="xs" wrap="nowrap">
					<PasswordInput
						style={{ flex: "1 1 auto", minWidth: 0 }}
						label={t("pages.externalProviders.editor.apiKeyLabel", "API key (optional)")}
						description={
							connection.hasApiKey
								? t("pages.externalProviders.editor.apiKeyStoredHint")
								: t("pages.externalProviders.editor.apiKeyHint")
						}
						placeholder={connection.hasApiKey ? "••••••••" : undefined}
						value={values.apiKey}
						data-testid="external-provider-api-key"
						onChange={(event) => {
							const value = event.currentTarget.value;
							dispatch({ type: "setField", field: "apiKey", value });
						}}
					/>
					{connection.hasApiKey ? (
						<Button
							variant="outline"
							color="red"
							leftSection={<IconTrash size={14} />}
							data-testid="external-provider-remove-key"
							onClick={() => dispatch({ type: "removeApiKey" })}
						>
							{t("pages.externalProviders.editor.removeApiKey", "Remove key")}
						</Button>
					) : null}
				</Group>
			)}

			<TextInput
				style={{ maxWidth: "16rem" }}
				inputMode="numeric"
				label={t("pages.externalProviders.editor.timeoutLabel", "Timeout (seconds)")}
				description={t("pages.externalProviders.editor.timeoutHint")}
				value={values.timeoutSeconds}
				data-testid="external-provider-timeout"
				onChange={(event) => {
					const value = event.currentTarget.value;
					dispatch({ type: "setField", field: "timeoutSeconds", value });
				}}
				onBlur={() => dispatch({ type: "touchField", field: "timeoutSeconds" })}
				error={visibleErrors.timeoutSeconds}
			/>

			<ExternalProviderProbePanel
				values={values}
				isStored={!connection.isNew}
				hasStoredApiKey={connection.hasApiKey}
				dispatch={dispatch}
			/>

			<ExternalProviderModelFields values={values} errors={visibleErrors} modelRowIds={modelRowIds} dispatch={dispatch} />

			<Group>
				<Button
					leftSection={<IconDeviceFloppy size={16} />}
					data-testid="external-provider-save"
					onClick={props.onSave}
					loading={status.isSaving}
					disabled={status.hasErrors || isActionPending}
				>
					{t("pages.externalProviders.editor.save", "Save connection")}
				</Button>
				{connection.isNew ? null : (
					<Button
						variant="outline"
						color="red"
						leftSection={<IconTrash size={16} />}
						data-testid="external-provider-delete"
						onClick={props.onDelete}
						loading={status.isDeleting}
						disabled={isActionPending}
					>
						{t("pages.externalProviders.editor.delete", "Delete connection")}
					</Button>
				)}
				<Button
					variant="subtle"
					leftSection={<IconX size={16} />}
					data-testid="external-provider-cancel"
					onClick={props.onCancel}
					disabled={isActionPending}
				>
					{t("pages.externalProviders.editor.cancel", "Cancel")}
				</Button>
			</Group>
		</SectionCard>
	);
}
