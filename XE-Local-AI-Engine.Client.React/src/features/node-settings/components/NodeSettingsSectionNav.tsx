import { Badge, Box, Button, NavLink, Select, Stack } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { NodeSettingsSectionId } from "@/features/node-settings/models/NodeSettingsSections";

interface Props {
	readonly sections: readonly NodeSettingsSectionId[];
	readonly active: NodeSettingsSectionId;
	readonly onSelect: (section: NodeSettingsSectionId) => void;
	// Unsaved-change count per section, so edits left behind in another section stay visible.
	readonly dirtyCounts: ReadonlyMap<NodeSettingsSectionId, number>;
	// Present only in Simple UI mode, where the advanced sections sit behind this toggle.
	readonly advancedToggle?: { readonly shown: boolean; readonly onToggle: () => void };
}

type Translate = ReturnType<typeof useTranslation>["t"];

function sectionLabel(t: Translate, section: NodeSettingsSectionId): string {
	const labels: Record<NodeSettingsSectionId, string> = {
		general: t("pages.nodeSettings.sections.general", "General"),
		chat: t("pages.nodeSettings.sections.chat", "Chat & agents"),
		runtime: t("pages.nodeSettings.sections.runtime", "Model runtime"),
		runtimes: t("pages.nodeSettings.sections.runtimes", "Runtimes & builds"),
		models: t("pages.nodeSettings.sections.models", "Models & downloads"),
		knowledge: t("pages.nodeSettings.sections.knowledge", "Knowledge & web"),
		voice: t("pages.nodeSettings.sections.voice", "Voice & transcription"),
		privacy: t("pages.nodeSettings.sections.privacy", "Privacy & updates"),
		integrations: t("pages.nodeSettings.sections.integrations", "Integrations & keys"),
		workspaces: t("pages.nodeSettings.sections.workspaces", "Agent workspaces"),
		sandbox: t("pages.nodeSettings.sections.sandbox", "Sandbox & isolation"),
		usage: t("pages.nodeSettings.sections.usage", "Usage & limits"),
	};
	return labels[section];
}

// A navigation landmark: a vertical list from the sm breakpoint up, a select on phone width.
export function NodeSettingsSectionNav({ sections, active, onSelect, dirtyCounts, advancedToggle }: Props) {
	const { t } = useTranslation();
	const label = (section: NodeSettingsSectionId): string => sectionLabel(t, section);
	const navLabel = t("pages.nodeSettings.sections.navLabel", "Settings sections");

	return (
		<Box component="nav" aria-label={navLabel} data-testid="node-settings-section-nav">
			<Select
				hiddenFrom="sm"
				aria-label={navLabel}
				data={sections.map((section) => ({ value: section, label: label(section) }))}
				value={active}
				onChange={(value) => {
					if (value !== null) {
						onSelect(value as NodeSettingsSectionId);
					}
				}}
				allowDeselect={false}
				data-testid="node-settings-section-select"
			/>
			<Stack gap={2} visibleFrom="sm">
				{sections.map((section) => {
					const dirty = dirtyCounts.get(section) ?? 0;
					return (
						<NavLink
							key={section}
							component="button"
							type="button"
							label={label(section)}
							active={section === active}
							aria-current={section === active ? "page" : undefined}
							onClick={() => onSelect(section)}
							rightSection={
								dirty > 0 ? (
									<Badge
										size="xs"
										circle={true}
										aria-label={t("pages.nodeSettings.saveBar.unsaved", "{{count}} unsaved changes", { count: dirty })}
									>
										{dirty}
									</Badge>
								) : null
							}
							data-testid={`node-settings-section-${section}`}
						/>
					);
				})}
			</Stack>
			{advancedToggle ? (
				<Button
					variant="subtle"
					size="xs"
					mt="xs"
					onClick={advancedToggle.onToggle}
					data-testid="node-settings-advanced-sections-toggle"
				>
					{advancedToggle.shown
						? t("pages.nodeSettings.sections.hideAdvanced", "Hide advanced sections")
						: t("pages.nodeSettings.sections.showAdvanced", "Show advanced sections")}
				</Button>
			) : null}
		</Box>
	);
}
