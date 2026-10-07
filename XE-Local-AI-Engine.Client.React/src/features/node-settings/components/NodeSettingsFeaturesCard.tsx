import { Card, Group, Stack, Switch, Text, Title } from "@mantine/core";
import { IconToggleRight } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import type { featureSwitchFields, NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

type FeatureSwitchField = (typeof featureSwitchFields)[number];

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

// The ten node feature switches. Development and Scheduler carry the restart badge (they decide what the host
// registers); the others apply on the next request, and their descriptions name the part that waits for a restart.
export function NodeSettingsFeaturesCard({ form, errors, onChange }: Props) {
	const { t } = useTranslation();

	const featureSwitch = (field: FeatureSwitchField, testId: string, label: string, description: string) => (
		<Switch
			key={field}
			label={nodeSettingsFieldLabel(t, field, label)}
			description={description}
			checked={form[field]}
			onChange={(event) => onChange(field, event.currentTarget.checked)}
			error={nodeSettingsFieldError(t, errors, field)}
			data-testid={`node-settings-feature-${testId}`}
		/>
	);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-features-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.features.title", "Features")}
					</Title>
					<IconToggleRight size={20} />
				</Group>
				<Text c="dimmed" size="sm">
					{t(
						"pages.nodeSettings.fields.features.description",
						"Turn whole features of this node on or off. A feature that is off is hidden from the navigation.",
					)}
				</Text>
				{featureSwitch(
					"developmentEnabled",
					"development",
					t("pages.nodeSettings.fields.developmentEnabled.label", "Development mode"),
					t(
						"pages.nodeSettings.fields.developmentEnabled.description",
						"Lets agents change code in registered source folders, each attempt in its own isolated worktree.",
					),
				)}
				{featureSwitch(
					"workSessionsEnabled",
					"work-sessions",
					t("pages.nodeSettings.fields.workSessionsEnabled.label", "Work sessions"),
					t(
						"pages.nodeSettings.fields.workSessionsEnabled.description",
						"Long-running agent sessions with their own plan, findings and checkpoints.",
					),
				)}
				{featureSwitch(
					"devWorkflowsEnabled",
					"dev-workflows",
					t("pages.nodeSettings.fields.devWorkflowsEnabled.label", "Development workflows"),
					t(
						"pages.nodeSettings.fields.devWorkflowsEnabled.description",
						"Durable, graph-based work items whose runs survive a restart. Needs work sessions. The built-in workflow definitions are seeded on the next restart.",
					),
				)}
				{featureSwitch(
					"graphWorkflowsEnabled",
					"graph-workflows",
					t("pages.nodeSettings.fields.graphWorkflowsEnabled.label", "Graph workflows"),
					t("pages.nodeSettings.fields.graphWorkflowsEnabled.description", "Author agent graphs on a canvas and run them."),
				)}
				{featureSwitch(
					"agentHomeEnabled",
					"agent-home",
					t("pages.nodeSettings.fields.agentHomeEnabled.label", "AgentHome workspaces"),
					t(
						"pages.nodeSettings.fields.agentHomeEnabled.description",
						"Lets agents run commands and edit files in a workspace of their own on this computer. Needs at least one tool-capable model.",
					),
				)}
				{featureSwitch(
					"computeEnabled",
					"compute",
					t("pages.nodeSettings.fields.computeEnabled.label", "Compute tools"),
					t(
						"pages.nodeSettings.fields.computeEnabled.description",
						"Lets agents run Python calculations in the managed Python environment. The Mathematician agent is seeded on the next restart.",
					),
				)}
				{featureSwitch(
					"externalAppsEnabled",
					"external-apps",
					t("pages.nodeSettings.fields.externalAppsEnabled.label", "External apps"),
					t(
						"pages.nodeSettings.fields.externalAppsEnabled.description",
						"Install and supervise curated applications on this computer. The API applies at once; the container bridge listener applies after a restart.",
					),
				)}
				{featureSwitch(
					"transcriptionEnabled",
					"transcription",
					t("pages.nodeSettings.fields.transcriptionEnabled.label", "Audio transcription"),
					t(
						"pages.nodeSettings.fields.transcriptionEnabled.description",
						"Transcribe recordings on this computer with whisper.cpp.",
					),
				)}
				{featureSwitch(
					"schedulerEnabled",
					"scheduler",
					t("pages.nodeSettings.fields.schedulerEnabled.label", "Scheduler"),
					t("pages.nodeSettings.fields.schedulerEnabled.description", "Run agents and jobs on a schedule."),
				)}
				{featureSwitch(
					"executionPreviewsEnabled",
					"execution-previews",
					t("pages.nodeSettings.fields.executionPreviewsEnabled.label", "Execution previews"),
					t(
						"pages.nodeSettings.fields.executionPreviewsEnabled.description",
						"Lets Preview sandbox mechanisms run Python and sandboxed MCP servers. On Windows this is the AppContainer boundary: no CPU, memory or process limit, only the time limit, and host path names stay visible. Applies to the next sandbox.",
					),
				)}
			</Stack>
		</Card>
	);
}
