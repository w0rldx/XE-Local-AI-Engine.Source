import { Card, Group, NumberInput, Stack, Text, Title } from "@mantine/core";
import { IconTool } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsFieldError,
	nodeSettingsRestartHint,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import type { NodeSettingsFieldBounds, NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

export interface NodeSettingsAdvancedFieldsCardProps {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

export function NodeSettingsAdvancedFieldsCard({ form, bounds, errors, onChange }: NodeSettingsAdvancedFieldsCardProps) {
	const { t } = useTranslation();
	const seconds = t("pages.nodeSettings.fields.seconds", "seconds");
	const minutes = t("pages.nodeSettings.fields.minutes", "minutes");
	const allowedRange = t("pages.nodeSettings.fields.allowedRange", "Allowed range");
	const byteLimitDescription = t(
		"pages.nodeSettings.fields.bytesPositive",
		"A positive number of bytes. Changes take effect without restarting the node.",
	);
	const numericFields = [
		{
			field: "orchestrationIdleTimeoutSeconds",
			label: t("pages.nodeSettings.fields.orchestrationIdleTimeoutSeconds.label", "Orchestration idle timeout"),
			description: (
				<>
					{`${allowedRange}: ${bounds.orchestrationIdleTimeoutSeconds.min}–${bounds.orchestrationIdleTimeoutSeconds.max} ${seconds}.`}
					{nodeSettingsRestartHint(t, "orchestrationIdleTimeoutSeconds")}
				</>
			),
			suffix: ` ${seconds}`,
			min: bounds.orchestrationIdleTimeoutSeconds.min,
			max: bounds.orchestrationIdleTimeoutSeconds.max,
			testId: "node-settings-orchestration-idle-timeout",
		},
		{
			field: "agentHomePrepareTimeoutSeconds",
			label: t("pages.nodeSettings.fields.agentHomePrepareTimeoutSeconds.label", "AgentHome prepare timeout"),
			description: `${allowedRange}: ${bounds.agentHomeTimeoutSeconds.min}–${bounds.agentHomeTimeoutSeconds.max} ${seconds}. ${t("pages.nodeSettings.fields.agentHomePrepareTimeoutSeconds.description", "Changes take effect without restarting the node.")}`,
			suffix: ` ${seconds}`,
			min: bounds.agentHomeTimeoutSeconds.min,
			max: bounds.agentHomeTimeoutSeconds.max,
			testId: "node-settings-agenthome-prepare-timeout",
		},
		{
			field: "agentHomeCommandTimeoutSeconds",
			label: t("pages.nodeSettings.fields.agentHomeCommandTimeoutSeconds.label", "AgentHome command timeout"),
			description: `${allowedRange}: ${bounds.agentHomeTimeoutSeconds.min}–${bounds.agentHomeTimeoutSeconds.max} ${seconds}. ${t("pages.nodeSettings.fields.agentHomeCommandTimeoutSeconds.description", "Changes take effect without restarting the node.")}`,
			suffix: ` ${seconds}`,
			min: bounds.agentHomeTimeoutSeconds.min,
			max: bounds.agentHomeTimeoutSeconds.max,
			testId: "node-settings-agenthome-command-timeout",
		},
		{
			field: "agentHomeMaxSelectedFolderBytes",
			label: t("pages.nodeSettings.fields.agentHomeMaxSelectedFolderBytes.label", "AgentHome max selected folder size"),
			description: byteLimitDescription,
			suffix: " bytes",
			min: 1,
			testId: "node-settings-agenthome-max-folder-bytes",
		},
		{
			field: "agentHomeMaxPatchBytes",
			label: t("pages.nodeSettings.fields.agentHomeMaxPatchBytes.label", "AgentHome max patch size"),
			description: byteLimitDescription,
			suffix: " bytes",
			min: 1,
			testId: "node-settings-agenthome-max-patch-bytes",
		},
		{
			field: "maxPendingToolCallAgeMinutes",
			label: t("pages.nodeSettings.fields.maxPendingToolCallAgeMinutes.label", "Max pending tool-call age"),
			description: (
				<>
					{`${allowedRange}: ${bounds.maxPendingToolCallAgeMinutes.min}–${bounds.maxPendingToolCallAgeMinutes.max} ${minutes}.`}
					{nodeSettingsRestartHint(t, "maxPendingToolCallAgeMinutes")}
				</>
			),
			suffix: ` ${minutes}`,
			min: bounds.maxPendingToolCallAgeMinutes.min,
			max: bounds.maxPendingToolCallAgeMinutes.max,
			testId: "node-settings-max-pending-toolcall-age",
		},
		{
			field: "detachedGraceSeconds",
			label: t("pages.nodeSettings.fields.detachedGraceSeconds.label", "Disconnect grace"),
			description: `${allowedRange}: ${bounds.detachedGraceSeconds.min}–${bounds.detachedGraceSeconds.max} ${seconds}. ${t("pages.nodeSettings.fields.detachedGraceSeconds.description", "How long a run keeps going after its last client disconnects. 0 never cancels.")}`,
			suffix: ` ${seconds}`,
			min: bounds.detachedGraceSeconds.min,
			max: bounds.detachedGraceSeconds.max,
			testId: "node-settings-detached-grace-seconds",
		},
	] as const;

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-advanced-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={4}>{t("pages.nodeSettings.fields.advanced.title", "Advanced (developer)")}</Title>
					<IconTool size={20} />
				</Group>
				<Text c="dimmed" size="sm">
					{t("pages.nodeSettings.fields.advanced.description", "Low-level orchestration and AgentHome limits.")}
				</Text>
				{numericFields.map(({ field, testId, ...inputProps }) => (
					<NumberInput
						key={field}
						{...inputProps}
						allowDecimal={false}
						value={form[field]}
						onChange={(value) => onChange(field, value)}
						error={nodeSettingsFieldError(t, errors, field)}
						data-testid={testId}
					/>
				))}
				<Text size="xs" c="dimmed">
					{t("pages.nodeSettings.fields.advanced.samplingNote", "Sampling defaults are configured per message during a chat.")}
				</Text>
			</Stack>
		</Card>
	);
}
