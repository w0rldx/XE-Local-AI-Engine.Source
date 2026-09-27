import { Card, Group, Select, Stack, Title } from "@mantine/core";
import { IconBox, IconDownload, IconMicrophone, IconSearch, IconTool, IconWorldDownload } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { nodeSettingsFieldLabel } from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import {
	containerRuntimeSelectValues,
	type NodeSettingsFieldBounds,
	type NodeSettingsFieldsForm,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The small cards for the curated runtime tunables, one per subsystem, placed by NodeSettingsFieldsCard.
export interface NodeSettingsTunableCardProps {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

function TunableCard({
	title,
	icon,
	testId,
	children,
}: {
	readonly title: string;
	readonly icon: ReactNode;
	readonly testId: string;
	readonly children: ReactNode;
}) {
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{title}
					</Title>
					{icon}
				</Group>
				{children}
			</Stack>
		</Card>
	);
}

export function NodeSettingsToolLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.toolLimits.title", "Tool and provider limits")}
			icon={<IconTool size={20} />}
			testId="node-settings-tool-limits-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="maxProviderCallsPerInvocation"
				label={t("pages.nodeSettings.fields.maxProviderCallsPerInvocation.label", "Model calls per agent run")}
				description={t(
					"pages.nodeSettings.fields.maxProviderCallsPerInvocation.description",
					"Stops an agent run that keeps calling the model, for example in a tool loop.",
				)}
				bounds={bounds.tunables.maxProviderCallsPerInvocation}
				testId="node-settings-max-provider-calls"
			/>
			<NodeSettingsNumberField
				{...field}
				field="customToolMaxTimeoutSeconds"
				label={t("pages.nodeSettings.fields.customToolMaxTimeoutSeconds.label", "Custom tool timeout ceiling")}
				description={t(
					"pages.nodeSettings.fields.customToolMaxTimeoutSeconds.description",
					"The longest timeout a custom tool may set for itself.",
				)}
				bounds={bounds.tunables.customToolMaxTimeoutSeconds}
				unit={t("pages.nodeSettings.fields.seconds", "seconds")}
				testId="node-settings-custom-tool-max-timeout"
			/>
		</TunableCard>
	);
}

export function NodeSettingsContainerRuntimeCard({ form, onChange }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.containerRuntime.title", "Container runtime")}
			icon={<IconBox size={20} />}
			testId="node-settings-container-runtime-card"
		>
			<Select
				label={nodeSettingsFieldLabel(
					t,
					"containerRuntimeSelection",
					t("pages.nodeSettings.fields.containerRuntimeSelection.label", "Runtime for external apps"),
				)}
				description={t(
					"pages.nodeSettings.fields.containerRuntimeSelection.description",
					"Which container runtime external apps use. An app can still override it.",
				)}
				data={containerRuntimeSelectValues.map((value) => ({
					value,
					label: t(`pages.nodeSettings.fields.containerRuntimeSelection.options.${value}`, value),
				}))}
				value={form.containerRuntimeSelection}
				onChange={(value) => onChange("containerRuntimeSelection", value ?? form.containerRuntimeSelection)}
				allowDeselect={false}
				data-testid="node-settings-container-runtime"
			/>
		</TunableCard>
	);
}

export function NodeSettingsDownloadLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.downloads.title", "Downloads")}
			icon={<IconDownload size={20} />}
			testId="node-settings-downloads-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="huggingFaceDownloadConnections"
				label={t("pages.nodeSettings.fields.huggingFaceDownloadConnections.label", "Parallel download connections")}
				bounds={bounds.tunables.huggingFaceDownloadConnections}
				testId="node-settings-hf-download-connections"
			/>
			<NodeSettingsNumberField
				{...field}
				field="huggingFaceDiskMarginBytes"
				label={t("pages.nodeSettings.fields.huggingFaceDiskMarginBytes.label", "Free disk space to keep")}
				description={t(
					"pages.nodeSettings.fields.huggingFaceDiskMarginBytes.description",
					"A download is refused if it would leave less free space than this.",
				)}
				bounds={bounds.huggingFaceDiskMarginBytes}
				unit="GB"
				wireUnit={t("pages.nodeSettings.fields.bytesShort", "B")}
				testId="node-settings-hf-disk-margin"
			/>
		</TunableCard>
	);
}

export function NodeSettingsKnowledgeSearchCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.knowledgeSearch.title", "Knowledge search")}
			icon={<IconSearch size={20} />}
			testId="node-settings-knowledge-search-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeSearchDefaultResults"
				label={t("pages.nodeSettings.fields.knowledgeSearchDefaultResults.label", "Default results")}
				description={t(
					"pages.nodeSettings.fields.knowledgeSearchDefaultResults.description",
					"Results returned when the model does not ask for a number. At most the maximum below.",
				)}
				bounds={bounds.tunables.knowledgeSearchDefaultResults}
				testId="node-settings-knowledge-default-results"
			/>
			<NodeSettingsNumberField
				{...field}
				field="knowledgeSearchMaxResults"
				label={t("pages.nodeSettings.fields.knowledgeSearchMaxResults.label", "Maximum results")}
				bounds={bounds.tunables.knowledgeSearchMaxResults}
				testId="node-settings-knowledge-max-results"
			/>
		</TunableCard>
	);
}

export function NodeSettingsWebFetchLimitsCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.webFetch.title", "Page fetching")}
			icon={<IconWorldDownload size={20} />}
			testId="node-settings-web-fetch-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="webFetchTimeoutSeconds"
				label={t("pages.nodeSettings.fields.webFetchTimeoutSeconds.label", "Page fetch timeout")}
				bounds={bounds.tunables.webFetchTimeoutSeconds}
				unit={t("pages.nodeSettings.fields.seconds", "seconds")}
				testId="node-settings-web-fetch-timeout"
			/>
			<NodeSettingsNumberField
				{...field}
				field="webFetchMaxContentChars"
				label={t("pages.nodeSettings.fields.webFetchMaxContentChars.label", "Page text limit")}
				description={t(
					"pages.nodeSettings.fields.webFetchMaxContentChars.description",
					"The most characters of a fetched page the model receives.",
				)}
				bounds={bounds.tunables.webFetchMaxContentChars}
				unit={t("pages.nodeSettings.fields.characters", "characters")}
				testId="node-settings-web-fetch-max-chars"
			/>
		</TunableCard>
	);
}

export function NodeSettingsTranscriptionCard({ bounds, ...field }: NodeSettingsTunableCardProps) {
	const { t } = useTranslation();
	const minutes = t("pages.nodeSettings.fields.minutes", "minutes");
	return (
		<TunableCard
			title={t("pages.nodeSettings.fields.transcription.title", "Transcription")}
			icon={<IconMicrophone size={20} />}
			testId="node-settings-transcription-card"
		>
			<NodeSettingsNumberField
				{...field}
				field="transcriptionIdleTimeoutMinutes"
				label={t("pages.nodeSettings.fields.transcriptionIdleTimeoutMinutes.label", "Unload an idle transcription model after")}
				bounds={bounds.tunables.transcriptionIdleTimeoutMinutes}
				unit={minutes}
				testId="node-settings-transcription-idle-timeout"
			/>
			<NodeSettingsNumberField
				{...field}
				field="transcriptionInferenceTimeoutMinutes"
				label={t("pages.nodeSettings.fields.transcriptionInferenceTimeoutMinutes.label", "Transcription timeout")}
				description={t(
					"pages.nodeSettings.fields.transcriptionInferenceTimeoutMinutes.description",
					"The longest one transcription job may run.",
				)}
				bounds={bounds.tunables.transcriptionInferenceTimeoutMinutes}
				unit={minutes}
				testId="node-settings-transcription-inference-timeout"
			/>
		</TunableCard>
	);
}
