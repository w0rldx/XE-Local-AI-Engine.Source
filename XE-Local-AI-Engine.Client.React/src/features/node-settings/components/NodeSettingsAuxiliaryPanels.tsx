import { Switch } from "@mantine/core";
import { IconCode } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { HfTokenPanel } from "@/features/node-settings/components/HfTokenPanel";
import { LocalModelProxyKeyPanel } from "@/features/node-settings/components/LocalModelProxyKeyPanel";
import { McpServerKeyPanel } from "@/features/node-settings/components/McpServerKeyPanel";
import { McpWorkspaceAllowlistPanel } from "@/features/node-settings/components/McpWorkspaceAllowlistPanel";

interface NodeSettingsAuxiliaryPanelsProps {
	readonly hasToken: boolean;
	readonly isTokenLoading: boolean;
	readonly tokenDraft: string;
	readonly isSavingToken: boolean;
	readonly onTokenDraftChange: (value: string) => void;
	readonly onSaveToken: () => void;
	readonly onClearToken: () => void;
}

export function NodeSettingsAuxiliaryPanels(props: NodeSettingsAuxiliaryPanelsProps) {
	return (
		<>
			<HfTokenPanel
				hasToken={props.hasToken}
				isLoading={props.isTokenLoading}
				tokenDraft={props.tokenDraft}
				onTokenDraftChange={props.onTokenDraftChange}
				onSave={props.onSaveToken}
				onClear={props.onClearToken}
				isSaving={props.isSavingToken}
			/>
			<McpServerKeyPanel />
			<LocalModelProxyKeyPanel />
			<McpWorkspaceAllowlistPanel />
		</>
	);
}

interface NodeSettingsDeveloperModePanelProps {
	readonly developerMode: boolean;
	readonly onToggleDeveloperMode: () => void;
}

export function NodeSettingsDeveloperModePanel(props: NodeSettingsDeveloperModePanelProps) {
	const { t } = useTranslation();
	return (
		<SectionCard title={t("pages.nodeSettings.developerMode.title", "Developer settings")} icon={<IconCode size={22} />}>
			<Switch
				label={t("pages.nodeSettings.developerMode.label", "Developer mode")}
				description={t(
					"pages.nodeSettings.developerMode.description",
					"Enables advanced, experimental controls in the app (e.g. chat sampling options), and starts recording this browser session — DOM changes, clicks and navigation — so it can be attached to a diagnostic snapshot. Stored in this browser only.",
				)}
				checked={props.developerMode}
				onChange={props.onToggleDeveloperMode}
				data-testid="developer-mode-switch"
			/>
		</SectionCard>
	);
}
