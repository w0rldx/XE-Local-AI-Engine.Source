import { Card, Group, NumberInput, Stack, Text, Title } from "@mantine/core";
import { IconFolderCog, IconTool } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsAllowedRange,
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import {
	type NodeSettingsFieldBounds,
	type NodeSettingsFieldsForm,
	nodeSettingsDisplayScale,
	toDisplayBounds,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The developer-only fields, as two cards: the agent-run limits (Chat & agents) and the AgentHome workspace limits
// (Agent workspaces). The page mounts either only while developer mode is on, so an off-mode save never touches them.
export interface NodeSettingsAdvancedFieldsCardProps {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

type NumericField =
	| "orchestrationIdleTimeoutSeconds"
	| "maxPendingToolCallAgeMinutes"
	| "detachedGraceSeconds"
	| "agentHomePrepareTimeoutSeconds"
	| "agentHomeCommandTimeoutSeconds"
	| "agentHomeMaxSelectedFolderBytes"
	| "agentHomeMaxPatchBytes";

interface NumericFieldSpec {
	readonly field: NumericField;
	readonly label: string;
	readonly description: string;
	readonly suffix: string;
	readonly min: number;
	readonly max?: number;
	readonly decimalScale?: number;
	readonly testId: string;
}

function AdvancedCard({
	title,
	icon,
	testId,
	specs,
	children,
	...props
}: NodeSettingsAdvancedFieldsCardProps & {
	readonly title: string;
	readonly icon: ReactNode;
	readonly testId: string;
	readonly specs: readonly NumericFieldSpec[];
	readonly children?: ReactNode;
}) {
	const { t } = useTranslation();
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{title}
					</Title>
					{icon}
				</Group>
				{specs.map(({ field, testId: fieldTestId, label, decimalScale, ...inputProps }) => (
					<NumberInput
						key={field}
						{...inputProps}
						label={nodeSettingsFieldLabel(t, field, label)}
						allowDecimal={decimalScale !== undefined}
						decimalScale={decimalScale}
						value={props.form[field]}
						onChange={(value) => props.onChange(field, value)}
						error={nodeSettingsFieldError(t, props.errors, field)}
						data-testid={fieldTestId}
					/>
				))}
				{children}
			</Stack>
		</Card>
	);
}

export function NodeSettingsAgentLimitsCard(props: NodeSettingsAdvancedFieldsCardProps) {
	const { t } = useTranslation();
	const { bounds } = props;
	const seconds = t("pages.nodeSettings.fields.seconds", "seconds");
	const minutes = t("pages.nodeSettings.fields.minutes", "minutes");
	const allowedRange = t("pages.nodeSettings.fields.allowedRange", "Allowed range");
	const specs: NumericFieldSpec[] = [
		{
			field: "orchestrationIdleTimeoutSeconds",
			label: t("pages.nodeSettings.fields.orchestrationIdleTimeoutSeconds.label", "Orchestration idle timeout"),
			description: `${allowedRange}: ${bounds.orchestrationIdleTimeoutSeconds.min}–${bounds.orchestrationIdleTimeoutSeconds.max} ${seconds}.`,
			suffix: ` ${seconds}`,
			min: bounds.orchestrationIdleTimeoutSeconds.min,
			max: bounds.orchestrationIdleTimeoutSeconds.max,
			testId: "node-settings-orchestration-idle-timeout",
		},
		{
			field: "maxPendingToolCallAgeMinutes",
			label: t("pages.nodeSettings.fields.maxPendingToolCallAgeMinutes.label", "Max pending tool-call age"),
			description: `${allowedRange}: ${bounds.maxPendingToolCallAgeMinutes.min}–${bounds.maxPendingToolCallAgeMinutes.max} ${minutes}.`,
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
	];

	return (
		<AdvancedCard
			{...props}
			title={t("pages.nodeSettings.fields.advanced.title", "Agent limits (developer)")}
			icon={<IconTool size={20} />}
			testId="node-settings-advanced-card"
			specs={specs}
		>
			<Text size="xs" c="dimmed">
				{t("pages.nodeSettings.fields.advanced.samplingNote", "Sampling defaults are configured per message during a chat.")}
			</Text>
		</AdvancedCard>
	);
}

export function NodeSettingsAgentWorkspacesCard(props: NodeSettingsAdvancedFieldsCardProps) {
	const { t } = useTranslation();
	const minutes = t("pages.nodeSettings.fields.minutes", "minutes");
	const secondsShort = t("pages.nodeSettings.fields.secondsShort", "s");
	const liveReload = t("pages.nodeSettings.fields.liveReload", "Changes take effect without restarting the node.");
	const timeoutBounds = toDisplayBounds(
		props.bounds.agentHomeTimeoutSeconds,
		nodeSettingsDisplayScale.agentHomePrepareTimeoutSeconds,
	);
	const timeoutDescription = `${nodeSettingsAllowedRange(
		t,
		props.bounds.agentHomeTimeoutSeconds,
		minutes,
		nodeSettingsDisplayScale.agentHomePrepareTimeoutSeconds,
		secondsShort,
	)} ${liveReload}`;
	const sizeDescription = t(
		"pages.nodeSettings.fields.megabytesPositive",
		"A positive size in MB. Changes take effect without restarting the node.",
	);
	const specs: NumericFieldSpec[] = [
		{
			field: "agentHomePrepareTimeoutSeconds",
			label: t("pages.nodeSettings.fields.agentHomePrepareTimeoutSeconds.label", "AgentHome prepare timeout"),
			description: timeoutDescription,
			suffix: ` ${minutes}`,
			min: timeoutBounds.min,
			max: timeoutBounds.max,
			decimalScale: 2,
			testId: "node-settings-agenthome-prepare-timeout",
		},
		{
			field: "agentHomeCommandTimeoutSeconds",
			label: t("pages.nodeSettings.fields.agentHomeCommandTimeoutSeconds.label", "AgentHome command timeout"),
			description: timeoutDescription,
			suffix: ` ${minutes}`,
			min: timeoutBounds.min,
			max: timeoutBounds.max,
			decimalScale: 2,
			testId: "node-settings-agenthome-command-timeout",
		},
		{
			field: "agentHomeMaxSelectedFolderBytes",
			label: t("pages.nodeSettings.fields.agentHomeMaxSelectedFolderBytes.label", "AgentHome max selected folder size"),
			description: sizeDescription,
			suffix: " MB",
			min: 0,
			decimalScale: 2,
			testId: "node-settings-agenthome-max-folder-bytes",
		},
		{
			field: "agentHomeMaxPatchBytes",
			label: t("pages.nodeSettings.fields.agentHomeMaxPatchBytes.label", "AgentHome max patch size"),
			description: sizeDescription,
			suffix: " MB",
			min: 0,
			decimalScale: 2,
			testId: "node-settings-agenthome-max-patch-bytes",
		},
	];

	return (
		<AdvancedCard
			{...props}
			title={t("pages.nodeSettings.fields.agentWorkspaces.title", "AgentHome workspaces (developer)")}
			icon={<IconFolderCog size={20} />}
			testId="node-settings-agent-workspaces-card"
			specs={specs}
		>
			<NodeSettingsNumberField
				field="agentHomeMaxRunSeconds"
				label={t("pages.nodeSettings.fields.agentHomeMaxRunSeconds.label", "AgentHome run time limit")}
				description={t(
					"pages.nodeSettings.fields.agentHomeMaxRunSeconds.description",
					"The longest a whole run may take; at least the command timeout. Applies to the next run.",
				)}
				bounds={props.bounds.agentHomeMaxRunSeconds}
				unit={minutes}
				wireUnit={secondsShort}
				form={props.form}
				errors={props.errors}
				onChange={props.onChange}
				testId="node-settings-agenthome-max-run"
			/>
			<NodeSettingsNumberField
				field="agentHomeRunRetentionDays"
				label={t("pages.nodeSettings.fields.agentHomeRunRetentionDays.label", "AgentHome run retention")}
				description={t(
					"pages.nodeSettings.fields.agentHomeRunRetentionDays.description",
					"Finished runs older than this are deleted.",
				)}
				bounds={props.bounds.agentHomeRunRetentionDays}
				unit={t("pages.nodeSettings.fields.days", "days")}
				form={props.form}
				errors={props.errors}
				onChange={props.onChange}
				testId="node-settings-agenthome-run-retention"
			/>
		</AdvancedCard>
	);
}
