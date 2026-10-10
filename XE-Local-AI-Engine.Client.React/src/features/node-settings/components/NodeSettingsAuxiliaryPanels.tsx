import {
	Button,
	ColorInput,
	DEFAULT_THEME,
	Group,
	Input,
	type MantineColorScheme,
	SegmentedControl,
	Select,
	Stack,
	Switch,
	Text,
	useMantineColorScheme,
} from "@mantine/core";
import { IconBrowser, IconExternalLink, IconLink } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { languageData } from "@/core/locales/models/LanguageMenuData";
import { useUserLanguageStore } from "@/core/locales/stores/UserLanguageStore";
import { useThemeStore } from "@/core/theme/stores/ThemeStore";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
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

// theme.json ships the primary as `rgb(222, 10, 27)`; ColorInput's hex format needs it as hex.
const shippedAccentHex = "#de0a1b";
const accentSwatches = [
	shippedAccentHex,
	DEFAULT_THEME.colors.blue[6],
	DEFAULT_THEME.colors.teal[6],
	DEFAULT_THEME.colors.green[6],
	DEFAULT_THEME.colors.violet[6],
	DEFAULT_THEME.colors.grape[6],
	DEFAULT_THEME.colors.orange[6],
	DEFAULT_THEME.colors.cyan[6],
];
const completeHexPattern = /^#[0-9a-f]{6}$/iu;

interface NodeSettingsBrowserPreferencesCardProps {
	readonly developerMode: boolean;
	readonly onToggleDeveloperMode: () => void;
}

// Preferences stored in this browser, not on the node: they apply instantly and are never part of the save bar.
export function NodeSettingsBrowserPreferencesCard(props: NodeSettingsBrowserPreferencesCardProps) {
	const { t, i18n } = useTranslation();
	const selectedLanguage = useUserLanguageStore((state) => state.selectedApplicationLanguage);
	const changeLanguage = useUserLanguageStore((state) => state.actions.changeLanguage);
	const { colorScheme, setColorScheme } = useMantineColorScheme();
	const accentColor = useThemeStore((state) => state.accentColor);
	const setAccentColor = useThemeStore((state) => state.setAccentColor);
	// The text the user is editing. Only a complete hex reaches the store, so the input needs its own draft, and it
	// must stay the same mounted element across the first choice and a reset: a key change would close the picker
	// mid-drag and drop focus mid-edit.
	const [accentDraft, setAccentDraft] = useState(accentColor ?? shippedAccentHex);
	const colorSchemeLabel = t("pages.nodeSettings.browserPreferences.colorScheme.label", "Colour scheme");

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
			<Stack gap={4}>
				<Input.Label>{colorSchemeLabel}</Input.Label>
				<SegmentedControl
					aria-label={colorSchemeLabel}
					value={colorScheme}
					onChange={(value) => setColorScheme(value as MantineColorScheme)}
					data={[
						{ value: "light", label: t("pages.nodeSettings.browserPreferences.colorScheme.light", "Light") },
						{ value: "dark", label: t("pages.nodeSettings.browserPreferences.colorScheme.dark", "Dark") },
						{ value: "auto", label: t("pages.nodeSettings.browserPreferences.colorScheme.system", "System") },
					]}
					data-testid="node-settings-color-scheme-control"
				/>
			</Stack>
			<Group gap="xs" align="flex-end">
				<ColorInput
					label={t("pages.nodeSettings.browserPreferences.accentColor.label", "Accent colour")}
					description={t(
						"pages.nodeSettings.browserPreferences.accentColor.description",
						"Used for buttons, links and highlights in this browser.",
					)}
					format="hex"
					swatches={accentSwatches}
					swatchesPerRow={8}
					withEyeDropper={true}
					// Mantine's eyedropper is an icon-only button with no name of its own.
					eyeDropperButtonProps={{
						"aria-label": t("pages.nodeSettings.browserPreferences.accentColor.eyeDropper", "Pick a colour from the screen"),
					}}
					value={accentDraft}
					// ColorInput emits partial text while the user types; only a complete hex is a choice.
					onChange={(value) => {
						setAccentDraft(value);
						if (completeHexPattern.test(value)) {
							setAccentColor(value);
						}
					}}
					data-testid="node-settings-accent-color-input"
				/>
				{accentColor === null ? null : (
					<Button
						variant="subtle"
						size="xs"
						onClick={() => {
							setAccentDraft(shippedAccentHex);
							setAccentColor(null);
						}}
						data-testid="node-settings-accent-color-reset"
					>
						{t("pages.nodeSettings.browserPreferences.accentColor.useDefault", "Use default")}
					</Button>
				)}
			</Group>
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
