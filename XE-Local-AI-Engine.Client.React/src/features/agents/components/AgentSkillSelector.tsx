import { Alert, Badge, Checkbox, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { SkillSummary } from "@/features/skills/models/SkillModels";
import { useSkills } from "@/features/skills/queries/useSkills";

interface AgentSkillSelectorProps {
	// Selected node skill ids, owned by the parent form. Mirrors AgentToolSelector's selectedToolNames.
	selectedSkillIds: readonly string[];
	onToggleSkill: (skillId: string, selected: boolean) => void;
}

// Synthesize a placeholder summary for a skill that is selected on the definition but no longer present in the live
// library (e.g. it was deleted). It is shown so the user can still see and deselect it; it is marked disabled and
// carries the id as its name so the orphan stays recognizable.
function orphanSkillEntry(id: string): SkillSummary {
	return {
		allowedTools: null,
		compatibility: null,
		createdAtUtc: 0,
		description: "",
		enabled: false,
		id,
		importedAtUtc: null,
		license: null,
		metadata: null,
		name: id,
		// An orphan's provenance is unknowable — it is Local so the placeholder never claims a source it does not have.
		origin: "Local",
		sourceUri: null,
		updatedAtUtc: 0,
		version: 0,
	};
}

// Skill multi-select for the agent form. The node skill library is fetched live (useSkills) — the SAME source the
// Skills page renders. Each row is a checkbox toggling membership in allowedSkillIds; disabled skills are shown but
// flagged (they are never loaded at resolve time even if assigned). Selected-but-absent skills are appended so they
// remain deselectable. Skills are sent to the agent's model on demand, so a privacy note is shown.
export function AgentSkillSelector({ selectedSkillIds, onToggleSkill }: AgentSkillSelectorProps) {
	const { t } = useTranslation();
	const skillsQuery = useSkills();

	// Render the live library plus any already-selected skills no longer in it (so they remain deselectable).
	// Orphan-selected skills are appended after the library, in selection order.
	const rows = useMemo<SkillSummary[]>(() => {
		const library = skillsQuery.data ?? [];
		const libraryIds = new Set(library.map((skill) => skill.id));
		const orphanSelected: SkillSummary[] = [];
		for (const id of selectedSkillIds) {
			if (!libraryIds.has(id)) {
				orphanSelected.push(orphanSkillEntry(id));
			}
		}
		return [...library, ...orphanSelected];
	}, [skillsQuery.data, selectedSkillIds]);
	const selectedSkillIdSet = useMemo(() => new Set(selectedSkillIds), [selectedSkillIds]);

	return (
		<Stack gap="xs" data-testid="agent-skill-selector">
			<Text size="sm" fw={600}>
				{t("pages.agents.form.skills.label", "Skills")}
			</Text>
			<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="agent-skill-privacy-note">
				{t(
					"pages.agents.form.skills.privacyNote",
					"Assigned skills are loaded by this agent's model on demand. If that model is a cloud provider, the skill content leaves this node.",
				)}
			</Alert>

			{skillsQuery.isLoading ? (
				<Group gap="sm" data-testid="agent-skill-loading">
					<Loader size="sm" />
					<Text c="dimmed" size="sm">
						{t("pages.agents.form.skills.loading", "Loading skills…")}
					</Text>
				</Group>
			) : null}

			{skillsQuery.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-skill-error">
					{t("pages.agents.form.skills.loadError", "Could not load the skill library.")}
				</Alert>
			) : null}

			{!skillsQuery.isLoading && !skillsQuery.error && rows.length === 0 ? (
				<Text size="xs" c="dimmed" data-testid="agent-skill-empty">
					{t("pages.agents.form.skills.empty", "No skills yet. Create skills on the Skills page to assign them here.")}
				</Text>
			) : null}

			{rows.map((skill) => {
				const isSelected = selectedSkillIdSet.has(skill.id);

				return (
					<Paper withBorder={true} p="xs" key={skill.id} data-testid={`agent-skill-row-${skill.id}`}>
						<Stack gap={4}>
							<Group justify="space-between" align="center" wrap="nowrap">
								<Checkbox
									checked={isSelected}
									label={
										<Group gap="xs" wrap="nowrap" align="center">
											<Text size="sm" fw={600} ff="monospace">
												{skill.name}
											</Text>
											{!skill.enabled ? (
												<Badge size="xs" variant="light" color="gray">
													{t("pages.agents.form.skills.disabledBadge", "disabled")}
												</Badge>
											) : null}
											{/* Provenance follows the skill to every surface it is chosen on, not just the library
											    page — assigning an imported skill is the moment the trust decision actually bites. */}
											{skill.origin === "Imported" ? (
												<Badge size="xs" variant="light" color="orange" data-testid={`agent-skill-imported-${skill.id}`}>
													{t("pages.skills.list.importedBadge", "Imported · {{source}}", {
														source: skill.sourceUri ?? t("pages.skills.list.importedUnknownSource", "an unknown source"),
													})}
												</Badge>
											) : null}
										</Group>
									}
									onChange={(event) => onToggleSkill(skill.id, event.currentTarget.checked)}
									data-testid={`agent-skill-checkbox-${skill.id}`}
								/>
							</Group>
							{skill.description ? (
								<Text size="xs" c="dimmed">
									{skill.description}
								</Text>
							) : null}
						</Stack>
					</Paper>
				);
			})}
		</Stack>
	);
}
