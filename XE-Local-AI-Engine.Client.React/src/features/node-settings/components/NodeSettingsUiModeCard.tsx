import { Group, SegmentedControl, Stack, Text, Title } from "@mantine/core";
import { IconLayoutSidebar } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";

interface Props {
	// The draft value as the server stores it. Anything but "simple" shows as Advanced, matching useUiMode.
	readonly value: string;
	readonly onChange: (value: "simple" | "advanced") => void;
}

// The home of the choice the first-run step promises can be changed here. It is a draft field like every other node
// setting: the save bar persists it, and the page seeds the node-settings cache with the save's response, which is
// what re-renders the navigation rail (it reads the same query) without a reload.
export function NodeSettingsUiModeCard({ value, onChange }: Props) {
	const { t } = useTranslation();
	const uiMode = value === "simple" ? "simple" : "advanced";

	return (
		<SectionCard
			title={t("pages.nodeSettings.uiMode.title", "Interface mode")}
			icon={<IconLayoutSidebar size={22} />}
			data-testid="node-settings-ui-mode-card"
		>
			<Text c="dimmed">
				{t(
					"pages.nodeSettings.uiMode.description",
					"Simple shows the everyday pages only; Advanced shows every page this build offers. This changes the navigation and nothing else — every page stays reachable by its own address, and nothing is deleted.",
				)}
			</Text>

			<Group>
				<SegmentedControl
					value={uiMode}
					onChange={(next) => onChange(next === "simple" ? "simple" : "advanced")}
					aria-label={t("pages.nodeSettings.uiMode.title", "Interface mode")}
					data={[
						{ value: "simple", label: t("pages.uiMode.simple.title", "Simple") },
						{ value: "advanced", label: t("pages.uiMode.advanced.title", "Advanced") },
					]}
					data-testid="node-settings-ui-mode-control"
				/>
			</Group>

			<Stack gap={2}>
				<Title order={3} size="h6">
					{uiMode === "simple" ? t("pages.uiMode.simple.title", "Simple") : t("pages.uiMode.advanced.title", "Advanced")}
				</Title>
				<Text size="sm" c="dimmed">
					{uiMode === "simple"
						? t(
								"pages.uiMode.simple.body",
								"Chat, agents, models, knowledge, images and transcription. The building and monitoring tools stay out of the way.",
							)
						: t(
								"pages.uiMode.advanced.body",
								"Everything, including tool and skill authoring, scheduling, workflows, integrations, benchmarks and training.",
							)}
				</Text>
			</Stack>
		</SectionCard>
	);
}
