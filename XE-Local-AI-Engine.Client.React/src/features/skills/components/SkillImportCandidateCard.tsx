import { Alert, Badge, Button, Card, Checkbox, Collapse, Group, List, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconBan, IconChevronDown, IconFileText } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsSkillsV1SkillImportCandidateResponse } from "@/core/api/generated";
import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";
import { SKILL_BODY_GUIDANCE_LINES } from "@/features/skills/models/SkillModels";

interface SkillImportCandidateCardProps {
	candidate: XeLocalAiEngineClientEndpointsSkillsV1SkillImportCandidateResponse;
	selected: boolean;
	onToggle: (name: string, selected: boolean) => void;
}

/**
 * One candidate skill in the import report — everything the operator has to judge before persisting it.
 *
 * The full body is expandable rather than always-rendered: a repo scan can return well over a hundred candidates,
 * and syntax-highlighting every body up front would cost far more than it shows. It is NOT truncated — expanding
 * yields the verbatim body, because reviewing the actual instructions is the only real audit this node offers.
 *
 * A candidate with a problem cannot be selected: the checkbox is disabled and the problem is stated, rather than
 * letting the operator tick a row the backend will refuse anyway.
 */
export function SkillImportCandidateCard({ candidate, selected, onToggle }: SkillImportCandidateCardProps) {
	const { t } = useTranslation();
	const [bodyOpen, setBodyOpen] = useState(false);

	const hasProblems = candidate.problems.length > 0;
	const isSelectable = candidate.canImport && !hasProblems;
	const isBodyOverGuidance = candidate.bodyLineCount > SKILL_BODY_GUIDANCE_LINES;

	return (
		<Card withBorder={true} radius="md" p="md" data-testid={`skill-import-candidate-${candidate.name}`}>
			<Stack gap="xs">
				<Group justify="space-between" align="flex-start" wrap="nowrap">
					<Checkbox
						checked={selected}
						disabled={!isSelectable}
						onChange={(event) => onToggle(candidate.name, event.currentTarget.checked)}
						data-testid={`skill-import-select-${candidate.name}`}
						label={
							<Text fw={600} ff="monospace">
								{candidate.name}
							</Text>
						}
						description={candidate.description}
					/>
					<Group gap={6} wrap="nowrap">
						{candidate.license ? (
							<Badge variant="light" color="gray" size="sm">
								{candidate.license}
							</Badge>
						) : null}
						{candidate.compatibility ? (
							<Badge variant="light" color="gray" size="sm">
								{candidate.compatibility}
							</Badge>
						) : null}
					</Group>
				</Group>

				<Text size="xs" c={isBodyOverGuidance ? "orange" : "dimmed"} data-testid={`skill-import-size-${candidate.name}`}>
					{t("pages.skills.import.candidate.size", "{{bytes}} bytes · {{lines}} lines", {
						bytes: candidate.bodySizeBytes.toLocaleString(),
						lines: candidate.bodyLineCount.toLocaleString(),
					})}
					{isBodyOverGuidance
						? ` · ${t("pages.skills.import.candidate.overGuidance", "over the {{lines}}-line guidance", {
								lines: SKILL_BODY_GUIDANCE_LINES,
							})}`
						: null}
				</Text>

				{candidate.allowedTools ? (
					<Text size="xs" c="dimmed" data-testid={`skill-import-allowed-tools-${candidate.name}`}>
						{t("pages.skills.import.candidate.allowedTools", "Declared allowed-tools: {{tools}}", {
							tools: candidate.allowedTools,
						})}
					</Text>
				) : null}

				{hasProblems ? (
					<Alert
						color="red"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.skills.import.candidate.problemsTitle", "Cannot be imported")}
						data-testid={`skill-import-problems-${candidate.name}`}
					>
						<List size="sm" withPadding={true}>
							{candidate.problems.map((problem) => (
								<List.Item key={problem}>{problem}</List.Item>
							))}
						</List>
					</Alert>
				) : null}

				{candidate.refusedScripts.length > 0 ? (
					<Alert
						color="red"
						variant="light"
						icon={<IconBan size={16} />}
						title={t("pages.skills.import.candidate.refusedTitle", "Refused — scripts are never imported")}
						data-testid={`skill-import-refused-${candidate.name}`}
					>
						<List size="sm" withPadding={true}>
							{candidate.refusedScripts.map((script) => (
								<List.Item key={script} ff="monospace">
									{script}
								</List.Item>
							))}
						</List>
					</Alert>
				) : null}

				{candidate.conflictsWithExistingSkill ? (
					<Alert
						color="blue"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						data-testid={`skill-import-conflict-${candidate.name}`}
					>
						{t("pages.skills.import.candidate.conflict", "A skill named '{{name}}' already exists on this node.", {
							name: candidate.name,
						})}
					</Alert>
				) : null}

				{candidate.resources.length > 0 ? (
					<Stack gap={2} data-testid={`skill-import-resources-${candidate.name}`}>
						<Text size="xs" fw={600} c="dimmed">
							{t("pages.skills.import.candidate.resources", "Bundled resources ({{count}})", {
								count: candidate.resources.length,
							})}
						</Text>
						<List size="xs" icon={<IconFileText size={12} />} withPadding={true}>
							{candidate.resources.map((resource) => (
								<List.Item key={resource.name}>
									<Text component="span" size="xs" ff="monospace">
										{resource.name}
									</Text>
									<Text component="span" size="xs" c="dimmed">
										{` · ${resource.mediaType} · ${resource.sizeBytes.toLocaleString()} B`}
									</Text>
								</List.Item>
							))}
						</List>
					</Stack>
				) : null}

				<Group>
					<Button
						size="compact-xs"
						variant="subtle"
						leftSection={<IconChevronDown size={12} />}
						onClick={() => setBodyOpen((open) => !open)}
						aria-expanded={bodyOpen}
						data-testid={`skill-import-body-toggle-${candidate.name}`}
					>
						{bodyOpen
							? t("pages.skills.import.candidate.hideBody", "Hide full body")
							: t("pages.skills.import.candidate.showBody", "View full body")}
					</Button>
				</Group>
				{/* keepMounted={false} so a body is only mounted (and Prism-tokenized) once the operator actually opens it —
				    a repo scan can carry 100+ candidates. */}
				<Collapse expanded={bodyOpen} keepMounted={false}>
					<div data-testid={`skill-import-body-${candidate.name}`}>
						<CodeBlock language="markdown" code={candidate.body} />
					</div>
				</Collapse>
			</Stack>
		</Card>
	);
}
