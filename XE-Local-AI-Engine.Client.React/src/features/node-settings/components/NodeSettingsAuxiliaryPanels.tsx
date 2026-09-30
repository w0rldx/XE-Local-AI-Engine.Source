import { Button, Group, Select, Switch, Text } from "@mantine/core";
import { IconBrowser, IconExternalLink, IconLink } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { useUserLanguageStore } from "@/core/locales/stores/UserLanguageStore";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { languageData } from "@/core/locales/models/LanguageMenuData";
import { LocalModelProxyKeyPanel } from "@/features/node-settings/components/LocalModelProxyKeyPanel";
import { McpServerKeyPanel } from "@/features/node-settings/components/McpServerKeyPanel";
import { McpWorkspaceAllowlistPanel } from "@/features/node-settings/components/McpWorkspaceAllowlistPanel";
import { NodeSettingsBrowserOnlyBadge } from "@/features/node-settings/components/NodeSettingsBrowserOnlyBadge";

// "Integrations & keys": the inbound credentials this node hands out, each with its own endpoint (outside the save
// bar), plus links to the pages that configure the outbound side.
export function NodeSettingsIntegrationPanels() {
	const { t } = useTranslation();
	return (
		<>
			<McpServerKeyPanel />
			<McpWorkspaceAllowlistPanel />
			<LocalModelProxyKeyPanel />
			<SectionCard
				title={t("pages.nodeSettings.integrationLinks.title", "Related settings")}
				icon={<IconLink size={22} />}
				data-testid="node-settings-integration-links"
			>
				<Text c="dimmed" size="sm">
					{t(
						"pages.nodeSettings.integrationLinks.description",
						"MCP servers, external model providers and cloud accounts have their own pages.",
					)}
				</Text>
				<Group gap="sm">
					<Button component={Link} to="/mcp" variant="light" size="xs" rightSection={<IconExternalLink size={14} />}>
						{t("pages.nodeSettings.integrationLinks.mcp", "MCP servers")}
					</Button>
					<Button
						component={Link}
						to="/external-providers"
						variant="light"
						size="xs"
						rightSection={<IconExternalLink size={14} />}
					>
						{t("pages.nodeSettings.integrationLinks.externalProviders", "External providers")}
					</Button>
					<Button component={Link} to="/cloud-settings" variant="light" size="xs" rightSection={<IconExternalLink size={14} />}>
						{t("pages.nodeSettings.integrationLinks.cloudSettings", "Cloud settings")}
					</Button>
				</Group>
			</SectionCard>
		</>
	);
}

interface NodeSettingsBrowserPreferencesCardProps {
	readonly developerMode: boolean;
	readonly onToggleDeveloperMode: () => void;
}

// Preferences stored in this browser, not on the node: they apply instantly and are never part of the save bar.
export function NodeSettingsBrowserPreferencesCard(props: NodeSettingsBrowserPreferencesCardProps) {
	const { t, i18n } = useTranslation();
	const selectedLanguage = useUserLanguageStore((state) => state.selectedApplicationLanguage);
	const changeLanguage = useUserLanguageStore((state) => state.actions.changeLanguage);

	const handleLanguageChange = async (language: string | null): Promise<void> => {
		if (language === null) {
			return;
		}
		await i18n.changeLanguage(language);
		changeLanguage(language);
	};

	return (
		<SectionCard
			title={t("pages.nodeSettings.browserPreferences.title", "This browser")}
			icon={<IconBrowser size={22} />}
			actions={<NodeSettingsBrowserOnlyBadge />}
			data-testid="node-settings-browser-preferences-card"
		>
			{languageData.length > 1 ? (
				<Select
					label={t("pages.nodeSettings.browserPreferences.languageLabel", "Language")}
					data={languageData.map((language) => ({ value: language.value, label: language.text }))}
					value={selectedLanguage}
					onChange={handleLanguageChange}
					allowDeselect={false}
					data-testid="node-settings-language-select"
				/>
			) : null}
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
