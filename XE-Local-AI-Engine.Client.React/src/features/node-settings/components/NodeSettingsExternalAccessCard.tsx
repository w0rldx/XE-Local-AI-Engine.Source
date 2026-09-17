import { Card, Group, Select, Stack, Switch, Text, Title } from "@mantine/core";
import { IconWorld } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ExternalAccessPreset, NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	// A profile pick is a command, not a field edit: it moves all three switches together and marks the preset pending
	// so the save carries the profile NAME alone and the server derives the triple.
	readonly onApplyPreset: (preset: ExternalAccessPreset) => void;
}

// The three settings that decide whether this node reaches the internet on its own, plus the profile that sets all
// three at once. No field here is restart-gated (the services read the setting live; the check itself runs once per
// process, which is what the "at startup" wording in the descriptions means), so no restart hint is rendered.
export function NodeSettingsExternalAccessCard({ form, onChange, onApplyPreset }: Props) {
	const { t } = useTranslation();

	// `custom` is display-only. The server stamps it whenever a save carries individual switches and no profile, so the
	// Select must be able to SHOW it — but it is disabled, because the client must never send it and the request
	// validator rejects it as an input.
	const profileOptions = [
		{ value: "recommended", label: t("pages.externalAccess.recommended.title", "Recommended") },
		{ value: "offline", label: t("pages.externalAccess.offline.title", "Offline / Manual") },
		{ value: "custom", label: t("pages.externalAccess.custom.title", "Custom"), disabled: true },
	];

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-external-access-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.externalAccess.title", "External access")}
					</Title>
					<IconWorld size={20} />
				</Group>
				<Select
					label={t("pages.nodeSettings.fields.externalAccess.profileLabel", "Profile")}
					description={t(
						"pages.nodeSettings.fields.externalAccess.profileDescription",
						"Picking a profile sets the three switches below. Changing a switch by hand sets the profile to Custom.",
					)}
					placeholder={t("pages.externalAccess.notChosen", "Not chosen")}
					data={profileOptions}
					value={form.externalAccessProfile === "" ? null : form.externalAccessProfile}
					onChange={(value) => {
						// The disabled `custom` entry cannot be picked, and null is the clear action; both are ignored rather
						// than turned into a save the server would reject.
						if (value === "recommended" || value === "offline") {
							onApplyPreset(value);
						}
					}}
					allowDeselect={false}
					data-testid="node-settings-external-access-profile"
				/>
				<Text c="dimmed" size="sm" data-testid="node-settings-external-access-does-not-block">
					{t(
						"pages.externalAccess.offline.doesNotBlock",
						"This is not a network switch. It does not block the connection to the C0re platform, MCP servers you have configured, or model-catalog lookups.",
					)}
				</Text>
				<Switch
					label={t("pages.nodeSettings.fields.autoCheckApplicationUpdates.label", "Check for application updates")}
					description={t(
						"pages.nodeSettings.fields.autoCheckApplicationUpdates.description",
						"Look for a newer version of the application at startup. You can always check by hand from the About dialog.",
					)}
					checked={form.autoCheckApplicationUpdates}
					onChange={(event) => onChange("autoCheckApplicationUpdates", event.currentTarget.checked)}
					data-testid="node-settings-auto-check-application-updates"
				/>
				<Switch
					label={t("pages.nodeSettings.fields.autoCheckRuntimeUpdates.label", "Check for llama.cpp runtime updates")}
					description={t(
						"pages.nodeSettings.fields.autoCheckRuntimeUpdates.description",
						"Look for a newer llama.cpp runtime build at startup. You can always check by hand from the runtime panel below.",
					)}
					checked={form.autoCheckRuntimeUpdates}
					onChange={(event) => onChange("autoCheckRuntimeUpdates", event.currentTarget.checked)}
					data-testid="node-settings-auto-check-runtime-updates"
				/>
				<Switch
					label={t("pages.nodeSettings.fields.autoProvisionFirstRunModel.label", "Download a starter model on first run")}
					description={t(
						"pages.nodeSettings.fields.autoProvisionFirstRunModel.description",
						"On a fresh install, fetch a model and runtime so the engine can answer straight away. Has no effect once a model is installed.",
					)}
					checked={form.autoProvisionFirstRunModel}
					onChange={(event) => onChange("autoProvisionFirstRunModel", event.currentTarget.checked)}
					data-testid="node-settings-auto-provision-first-run-model"
				/>
			</Stack>
		</Card>
	);
}
