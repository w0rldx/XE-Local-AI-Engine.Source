import { ActionIcon, Badge, Group, Table, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { SkillSummary } from "@/features/skills/models/SkillModels";

interface SkillListProps {
	skills: readonly SkillSummary[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (skill: SkillSummary) => void;
}

// Table of node skills with edit + delete row actions. Pure presentation — the parent owns the data and the action
// handlers. Enabled state is shown as a badge (toggling enabled happens in the editor, mirroring the form contract);
// the list endpoint omits the body, so only the name + description summary is shown here.
export function SkillList({ skills, isMutating, onEdit, onDelete }: SkillListProps) {
	const { t } = useTranslation();

	if (skills.length === 0) {
		return (
			<Text c="dimmed" data-testid="skills-empty">
				{t("pages.skills.list.empty", "No skills yet. Create one to give your agents reusable expertise.")}
			</Text>
		);
	}

	return (
		<Table.ScrollContainer minWidth={640}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="skills-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.skills.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.skills.list.columns.enabled", "Enabled")}</Table.Th>
						<Table.Th>{t("pages.skills.list.columns.version", "Version")}</Table.Th>
						<Table.Th>{t("pages.skills.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{skills.map((skill) => (
						<Table.Tr key={skill.id} data-testid={`skill-row-${skill.id}`}>
							<Table.Td>
								<Text fw={600} ff="monospace">
									{skill.name}
								</Text>
								{skill.description ? (
									<Text size="xs" c="dimmed" lineClamp={1}>
										{skill.description}
									</Text>
								) : null}
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color={skill.enabled ? "teal" : "gray"}>
									{skill.enabled
										? t("pages.skills.list.enabledBadge", "Enabled")
										: t("pages.skills.list.disabledBadge", "Disabled")}
								</Badge>
							</Table.Td>
							<Table.Td>{skill.version}</Table.Td>
							<Table.Td>
								<Group gap="xs">
									<ActionIcon
										aria-label={t("pages.skills.list.editAria", "Edit {{name}}", { name: skill.name })}
										variant="subtle"
										disabled={isMutating}
										onClick={() => onEdit(skill.id)}
										data-testid={`skill-edit-${skill.id}`}
									>
										<IconPencil size={16} />
									</ActionIcon>
									<ActionIcon
										aria-label={t("pages.skills.list.deleteAria", "Delete {{name}}", { name: skill.name })}
										variant="subtle"
										color="red"
										disabled={isMutating}
										onClick={() => onDelete(skill)}
										data-testid={`skill-delete-${skill.id}`}
									>
										<IconTrash size={16} />
									</ActionIcon>
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
