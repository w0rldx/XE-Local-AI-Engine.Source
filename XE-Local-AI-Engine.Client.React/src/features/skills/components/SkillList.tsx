import { ActionIcon, Badge, Group, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { isSkillNameResolvable, type SkillSummary } from "@/features/skills/models/SkillModels";

interface SkillListProps {
	skills: readonly SkillSummary[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (skill: SkillSummary) => void;
}

// Table of node skills with edit + delete row actions. Pure presentation — the parent owns the data and the action
// handlers. Enabled state is shown as a badge (toggling enabled happens in the editor, mirroring the form contract);
// the list endpoint omits the body, so only the name + description summary is shown here.
//
// Two provenance/health signals ride on the name cell. An `Imported` badge carries the source, so the trust decision
// an operator made at import time stays visible long afterwards. A name the resolver would reject is flagged: such a
// row is DROPPED when an agent is built (fail-soft, logged, never thrown), so the skill silently does nothing until
// it is renamed — the flag is the only place that becomes visible.
//
// There is deliberately no resource-count column: the list projection cannot populate it, so it would read a constant
// zero. Resource counts belong on the detail view, which fetches them.
export function SkillList({ skills, isMutating, onEdit, onDelete }: SkillListProps) {
	const { t } = useTranslation();

	return (
		<ResponsiveTable
			rows={skills}
			rowKey={(skill) => skill.id}
			rowTestId={(skill) => `skill-row-${skill.id}`}
			minWidth={640}
			data-testid="skills-table"
			empty={
				<Text c="dimmed" data-testid="skills-empty">
					{t("pages.skills.list.empty", "No skills yet. Create one to give your agents reusable expertise.")}
				</Text>
			}
			columns={[
				{
					key: "name",
					header: t("pages.skills.list.columns.name", "Name"),
					primary: true,
					cell: (skill) => (
						<>
							<Group gap="xs">
								<Text fw={600} ff="monospace">
									{skill.name}
								</Text>
								{skill.origin === "Imported" ? (
									<Badge variant="light" color="orange" size="sm" data-testid={`skill-imported-badge-${skill.id}`}>
										{t("pages.skills.list.importedBadge", "Imported · {{source}}", {
											source: skill.sourceUri ?? t("pages.skills.list.importedUnknownSource", "an unknown source"),
										})}
									</Badge>
								) : null}
								{isSkillNameResolvable(skill.name) ? null : (
									<Tooltip
										multiline={true}
										w={280}
										label={t(
											"pages.skills.list.invalidNameTooltip",
											"This name is not valid for an agent, so the skill is skipped whenever an agent is built. Rename it to lowercase letters and digits separated by single dashes.",
										)}
									>
										<Badge
											variant="light"
											color="red"
											size="sm"
											leftSection={<IconAlertTriangle size={11} />}
											data-testid={`skill-invalid-name-${skill.id}`}
										>
											{t("pages.skills.list.invalidNameBadge", "Name not usable")}
										</Badge>
									</Tooltip>
								)}
							</Group>
							{skill.description ? (
								<Text size="xs" c="dimmed" lineClamp={1}>
									{skill.description}
								</Text>
							) : null}
						</>
					),
				},
				{
					key: "enabled",
					header: t("pages.skills.list.columns.enabled", "Enabled"),
					cell: (skill) => (
						<Badge variant="light" color={skill.enabled ? "teal" : "gray"} style={{ minWidth: "max-content" }}>
							{skill.enabled ? t("pages.skills.list.enabledBadge", "Enabled") : t("pages.skills.list.disabledBadge", "Disabled")}
						</Badge>
					),
				},
				{ key: "version", header: t("pages.skills.list.columns.version", "Version"), cell: (skill) => skill.version },
			]}
			actionsHeader={t("pages.skills.list.columns.actions", "Actions")}
			actions={(skill) => (
				<>
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
				</>
			)}
		/>
	);
}
