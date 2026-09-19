import { List, Text, ThemeIcon } from "@mantine/core";
import { IconCircleCheck, IconCircleX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { LlamaCppSourceBuildPrerequisiteItem } from "@/features/node-settings/models/SourceBuildModels";
import { sourceBuildPrerequisiteDiagnostic } from "@/features/node-settings/models/SourceBuildModels";

// The toolchain checklist the three source-build cards render identically: one row per probe, with the tool's own
// version banner kept as a diagnostic and the backend's English availability prose dropped in favour of a localized
// Available / Missing. Each card keeps its OWN i18n subtree (the runtime names differ in the surrounding copy), so
// the prefix is a prop rather than a shared namespace — the markup is what is shared, not the strings.

export interface SourceBuildPrerequisiteListProps {
	readonly items: readonly LlamaCppSourceBuildPrerequisiteItem[];
	/** The card's `pages.nodeSettings.<runtime>.sourceBuild` key prefix; `prerequisites.*` and
	 * `prerequisiteAvailability.*` are resolved under it. */
	readonly translationPrefix: string;
}

export function SourceBuildPrerequisiteList({ items, translationPrefix }: SourceBuildPrerequisiteListProps) {
	const { t } = useTranslation();

	return (
		<List spacing="xs" size="sm">
			{items.map((item) => {
				const diagnostic = sourceBuildPrerequisiteDiagnostic(item);
				return (
					<List.Item
						key={item.key}
						icon={
							<ThemeIcon color={item.satisfied ? "green" : "red"} size={20} radius="xl" variant="light">
								{item.satisfied ? <IconCircleCheck size={14} /> : <IconCircleX size={14} />}
							</ThemeIcon>
						}
					>
						<Text span={true} fw={500}>
							{t(`${translationPrefix}.prerequisites.${item.key}`, item.key)}
						</Text>{" "}
						<Text span={true} c="dimmed">
							{t(`${translationPrefix}.prerequisiteAvailability.${item.satisfied ? "available" : "missing"}`)}
							{diagnostic ? ` · ${diagnostic}` : ""}
						</Text>
					</List.Item>
				);
			})}
		</List>
	);
}
