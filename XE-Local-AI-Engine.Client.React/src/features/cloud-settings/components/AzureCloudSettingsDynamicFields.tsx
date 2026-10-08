import { ActionIcon, Button, Flex, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import type { Dispatch } from "react";
import { useTranslation } from "react-i18next";

import { CustomHeaderRows } from "@/core/ui/components/CustomHeaderRows/CustomHeaderRows";
import { type CloudSettingsFormAction, nextCloudRowId } from "@/features/cloud-settings/models/CloudSettingsFormState";
import type { CloudSettingsFormValues } from "@/features/cloud-settings/models/CloudSettingsModel";

interface AzureCloudSettingsDynamicFieldsProps {
	readonly values: CloudSettingsFormValues;
	readonly errors: Partial<Record<keyof CloudSettingsFormValues, string>>;
	readonly modelRowIds: readonly string[];
	readonly headerRowIds: readonly string[];
	readonly hostSuffixRowIds: readonly string[];
	readonly dispatch: Dispatch<CloudSettingsFormAction>;
}

export function AzureCloudSettingsDynamicFields(props: AzureCloudSettingsDynamicFieldsProps) {
	const { t } = useTranslation();
	const { values: formValues, errors: visibleErrors, modelRowIds, headerRowIds, hostSuffixRowIds, dispatch } = props;
	return (
		<>
			<Stack gap={6}>
				<Text size="sm" fw={500}>
					{t("pages.cloudSettings.azure.modelsLabel", "Models")}
				</Text>
				<Text size="xs" c="dimmed">
					{t("pages.cloudSettings.azure.deploymentNameHelp")}
				</Text>
				{formValues.models.map((model, index) => (
					<Group key={modelRowIds[index]} align="flex-end" gap="xs" wrap="nowrap">
						<Flex
							direction={{ base: "column", sm: "row" }}
							gap="xs"
							align={{ base: "stretch", sm: "flex-end" }}
							style={{ flex: "1 1 auto", minWidth: 0 }}
						>
							<TextInput
								style={{ flex: "1 1 auto", minWidth: 0 }}
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
								style={{ flex: "1 1 auto", minWidth: 0 }}
								aria-label={t("pages.cloudSettings.azure.displayLabelLabel", "Display label (optional)")}
								label={index === 0 ? t("pages.cloudSettings.azure.displayLabelLabel", "Display label (optional)") : undefined}
								placeholder={t("pages.cloudSettings.azure.displayLabelPlaceholder", "GPT-4o")}
								value={model.displayLabel}
								onChange={(event) => {
									const value = event.currentTarget.value;
									dispatch({ type: "setModelField", index, field: "displayLabel", value });
								}}
							/>
						</Flex>
						<ActionIcon
							variant="subtle"
							color="red"
							size="lg"
							data-testid={`cloud-settings-remove-model-${index}`}
							aria-label={t("pages.cloudSettings.azure.removeModel", "Remove model")}
							onClick={() => dispatch({ type: "removeModel", index, replacementRowId: nextCloudRowId("cloud-model") })}
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
						onClick={() => dispatch({ type: "addModel", rowId: nextCloudRowId("cloud-model") })}
					>
						{t("pages.cloudSettings.azure.addModel", "Add model")}
					</Button>
				</Group>
			</Stack>

			<CustomHeaderRows
				headers={formValues.headers}
				rowIds={headerRowIds}
				title={t("pages.cloudSettings.azure.headers.title", "Custom headers")}
				description={t("pages.cloudSettings.azure.headers.description")}
				namePlaceholder={t("pages.cloudSettings.azure.headers.namePlaceholder", "Ocp-Apim-Subscription-Key")}
				error={visibleErrors.headers ? { message: visibleErrors.headers } : undefined}
				testIdPrefix="cloud-settings"
				onAdd={() => dispatch({ type: "addHeader", rowId: nextCloudRowId("cloud-header") })}
				onRemove={(index) => dispatch({ type: "removeHeader", index })}
				onChange={(index, field, value) => dispatch({ type: "setHeaderField", index, field, value })}
				onToggleSecret={(index) => dispatch({ type: "toggleHeaderSecret", index })}
				onBlur={() => dispatch({ type: "touchField", field: "headers" })}
			/>

			<Stack gap={6}>
				<Text size="sm" fw={500}>
					{t("pages.cloudSettings.azure.hostSuffixes.title", "Allowed host suffixes")}
				</Text>
				<Text size="xs" c="dimmed">
					{t("pages.cloudSettings.azure.hostSuffixes.description")}
				</Text>
				{formValues.hostSuffixes.map((suffix, index) => (
					<Group key={hostSuffixRowIds[index]} align="flex-end" gap="xs" wrap="nowrap">
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
						onClick={() => dispatch({ type: "addHostSuffix", rowId: nextCloudRowId("cloud-host") })}
					>
						{t("pages.cloudSettings.azure.hostSuffixes.addHost", "Add allowed host")}
					</Button>
				</Group>
			</Stack>
		</>
	);
}
