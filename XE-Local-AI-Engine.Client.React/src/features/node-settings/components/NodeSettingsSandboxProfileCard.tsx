import { Divider, Switch, Text } from "@mantine/core";
import { IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import {
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { SandboxProfileRadioCards } from "@/features/node-settings/components/SandboxProfileRadioCards";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	// The profile the node is enforcing now, from the last-loaded settings (the draft may differ until saved).
	readonly effectiveProfile: string | undefined;
	// The workloads `high` would refuse on this host; undefined while the capability read is loading or failed.
	readonly highRefusals: readonly string[] | undefined;
}

// The sandbox security profile and the execution-previews switch: two independent settings that both decide what a
// sandboxed workload may run under, so they are reviewed together. Lowering the profile and turning previews on are
// each confirmed in the page's save flow.
export function NodeSettingsSandboxProfileCard({ form, errors, onChange, effectiveProfile, highRefusals }: Props) {
	const { t } = useTranslation();
	const draft =
		form.sandboxSecurityProfile === "high" || form.sandboxSecurityProfile === "low" ? form.sandboxSecurityProfile : null;
	const profileLabel = (profile: string | undefined): string =>
		profile === "high"
			? t("pages.sandboxProfile.high.title", "High")
			: profile === "low"
				? t("pages.sandboxProfile.low.title", "Low")
				: t("pages.nodeSettings.sandboxProfile.notChosen", "Not chosen");

	return (
		<SectionCard
			title={t("pages.nodeSettings.sandboxProfile.title", "Sandbox security profile")}
			icon={<IconShieldLock size={22} />}
			data-testid="node-settings-sandbox-profile-card"
		>
			<Text c="dimmed">
				{t(
					"pages.nodeSettings.sandboxProfile.description",
					"Decides whether the boundaries a workload asks for are preferences or requirements. A change applies to the next sandbox.",
				)}
			</Text>
			<Text size="sm" data-testid="node-settings-sandbox-profile-effective">
				{t("pages.nodeSettings.sandboxProfile.effective", "Effective profile: {{profile}}", {
					profile: profileLabel(effectiveProfile),
				})}
			</Text>

			{/* The refusals are shown only while the draft is High: that is the moment they are a consequence of the choice. */}
			<SandboxProfileRadioCards
				value={draft}
				onChange={(value) => onChange("sandboxSecurityProfile", value)}
				recommended={null}
				highRefusals={draft === "high" ? highRefusals : undefined}
				testIdPrefix="node-settings-sandbox-profile"
			/>

			<Divider />

			<Switch
				label={nodeSettingsFieldLabel(
					t,
					"executionPreviewsEnabled",
					t("pages.nodeSettings.fields.executionPreviewsEnabled.label", "Execution previews"),
				)}
				description={t(
					"pages.nodeSettings.fields.executionPreviewsEnabled.description",
					"Lets Preview sandbox mechanisms run Python and sandboxed MCP servers. On Windows this is the AppContainer boundary: no CPU, memory or process limit, only the time limit, and host path names stay visible. Applies to the next sandbox.",
				)}
				checked={form.executionPreviewsEnabled}
				onChange={(event) => onChange("executionPreviewsEnabled", event.currentTarget.checked)}
				error={nodeSettingsFieldError(t, errors, "executionPreviewsEnabled")}
				data-testid="node-settings-feature-execution-previews"
			/>
			<Text size="sm" c="dimmed">
				{t("pages.sandboxProfile.previewsNote", "Execution previews stay a separate setting: neither profile turns them on.")}
			</Text>
		</SectionCard>
	);
}
