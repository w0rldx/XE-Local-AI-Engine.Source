import { Alert, Badge, Button, Card, Checkbox, Collapse, Group, List, Skeleton, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconBan, IconChevronDown, IconEye, IconEyeOff, IconFileOff } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import type {
	XeLocalAiEngineClientEndpointsSkillsV1SkillImportCandidateResponse,
	XeLocalAiEngineClientEndpointsSkillsV1SkillResourceSummaryResponse,
} from "@/core/api/generated";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SKILL_BODY_GUIDANCE_LINES } from "@/features/skills/models/SkillModels";
import { useSkillImportPreviewResource } from "@/features/skills/queries/useSkillImport";

interface SkillImportCandidateCardProps {
	candidate: XeLocalAiEngineClientEndpointsSkillsV1SkillImportCandidateResponse;
	/** The preview token; resource content is read from the cached preview by it, never inlined into the report. */
	token: string;
	selected: boolean;
	onToggle: (name: string, selected: boolean) => void;
}

/**
 * One candidate skill in the import report — everything the operator has to judge before persisting it.
 *
 * The full body and each resource are expandable rather than always-rendered: a repo scan can return well over a
 * hundred candidates, and mounting an editor per body up front would cost far more than it shows. Nothing is
 * truncated — expanding yields the verbatim text in the inspection editor (hidden and look-alike characters drawn),
 * because reviewing the actual instructions is the only real audit this node offers. The manifest lists what was
 * accepted, what was refused (scripts) and what was dropped (unsupported file types), so nothing leaves silently.
 *
 * A candidate with a problem cannot be selected: the checkbox is disabled and the problem is stated, rather than
 * letting the operator tick a row the backend will refuse anyway.
 */
export function SkillImportCandidateCard({ candidate, token, selected, onToggle }: SkillImportCandidateCardProps) {
	const { t } = useTranslation();
	const [bodyOpen, setBodyOpen] = useState(false);

	const hasProblems = candidate.problems.length > 0;
	const isSelectable = candidate.canImport && !hasProblems;
	const isBodyOverGuidance = candidate.bodyLineCount > SKILL_BODY_GUIDANCE_LINES;
	const metadata = Object.entries(candidate.metadata ?? {});

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
					<InlineErrorAlert
						message={
							<List size="sm" withPadding={true}>
								{candidate.problems.map((problem) => (
									<List.Item key={problem}>{problem}</List.Item>
								))}
							</List>
						}
						variant="light"
						title={t("pages.skills.import.candidate.problemsTitle", "Cannot be imported")}
						data-testid={`skill-import-problems-${candidate.name}`}
					/>
				) : null}

				{candidate.refusedScripts.length > 0 ? (
					<InlineErrorAlert
						variant="light"
						icon={<IconBan size={16} />}
						title={t("pages.skills.import.candidate.refusedTitle", "Refused — scripts are never imported")}
						data-testid={`skill-import-refused-${candidate.name}`}
						message={
							<List size="sm" withPadding={true}>
								{candidate.refusedScripts.map((script) => (
									<List.Item key={script} ff="monospace">
										{script}
									</List.Item>
								))}
							</List>
						}
					/>
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

				{candidate.ignoredFiles.length > 0 ? (
					<Alert
						color="gray"
						variant="light"
						icon={<IconFileOff size={16} />}
						title={t("pages.skills.import.candidate.ignoredTitle", "Ignored — unsupported file types are not imported")}
						data-testid={`skill-import-ignored-${candidate.name}`}
					>
						<List size="sm" withPadding={true}>
							{candidate.ignoredFiles.map((file) => (
								<List.Item key={file} ff="monospace">
									{file}
								</List.Item>
							))}
						</List>
					</Alert>
				) : null}

				{metadata.length > 0 ? (
					<Stack gap={2} data-testid={`skill-import-metadata-${candidate.name}`}>
						<Text size="xs" fw={600} c="dimmed">
							{t("pages.skills.import.candidate.metadata", "Metadata ({{count}})", { count: metadata.length })}
						</Text>
						<List size="xs" withPadding={true}>
							{metadata.map(([key, value]) => (
								<List.Item key={key}>
									<Text component="span" size="xs" ff="monospace">
										{`${key}: ${value}`}
									</Text>
								</List.Item>
							))}
						</List>
					</Stack>
				) : null}

				{candidate.resources.length > 0 ? (
					<Stack gap={4} data-testid={`skill-import-resources-${candidate.name}`}>
						<Text size="xs" fw={600} c="dimmed">
							{t("pages.skills.import.candidate.resources", "Bundled resources ({{count}})", {
								count: candidate.resources.length,
							})}
						</Text>
						{candidate.resources.map((resource) => (
							<SkillImportResourceRow key={resource.name} token={token} skillName={candidate.name} resource={resource} />
						))}
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
				{/* keepMounted={false} so a body's editor is only mounted once the operator actually opens it — a repo scan
				    can carry 100+ candidates. */}
				<Collapse expanded={bodyOpen} keepMounted={false}>
					<CodeEditor
						inspect={true}
						language="markdown"
						wordWrap={true}
						value={candidate.body}
						aria-label={t("pages.skills.import.candidate.bodyLabel", "Body of {{name}}", { name: candidate.name })}
						data-testid={`skill-import-body-${candidate.name}`}
					/>
				</Collapse>
			</Stack>
		</Card>
	);
}

interface SkillImportResourceRowProps {
	readonly token: string;
	readonly skillName: string;
	readonly resource: XeLocalAiEngineClientEndpointsSkillsV1SkillResourceSummaryResponse;
}

/** One bundled resource: its summary, and on demand its content in the inspection editor. */
function SkillImportResourceRow({ token, skillName, resource }: SkillImportResourceRowProps) {
	const { t } = useTranslation();
	const [open, setOpen] = useState(false);
	const content = useSkillImportPreviewResource(token, skillName, open ? resource.name : null);
	const testIdSuffix = `${skillName}-${resource.name}`;

	return (
		<Stack gap={4} data-testid={`skill-import-resource-${testIdSuffix}`}>
			<Group gap="xs" wrap="nowrap" justify="space-between">
				<Text size="xs" ff="monospace" style={{ minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>
					{resource.name}
				</Text>
				<Group gap="xs" wrap="nowrap">
					<Text size="xs" c="dimmed">
						{`${resource.mediaType} · ${resource.sizeBytes.toLocaleString()} B`}
					</Text>
					<Button
						size="compact-xs"
						variant="subtle"
						leftSection={open ? <IconEyeOff size={12} /> : <IconEye size={12} />}
						onClick={() => setOpen((current) => !current)}
						aria-expanded={open}
						data-testid={`skill-import-resource-toggle-${testIdSuffix}`}
					>
						{open ? t("common.hide", "Hide") : t("common.view", "View")}
					</Button>
				</Group>
			</Group>
			{open ? (
				content.isPending ? (
					<Skeleton height={240} radius="sm" data-testid={`skill-import-resource-loading-${testIdSuffix}`} />
				) : content.error ? (
					<InlineErrorAlert
						data-testid={`skill-import-resource-error-${testIdSuffix}`}
						message={
							// 404 here means the preview token expired (15 minutes) or a commit consumed it: say what to do
							// rather than echo a bare "Not Found".
							getErrorStatus(content.error) === 404
								? t(
										"pages.skills.import.candidate.resourceExpired",
										"This preview has expired or was already imported, so the resource can no longer be read here. Choose the source again to re-run the preview.",
									)
								: apiErrorMessage(
										content.error,
										t("pages.skills.import.candidate.resourceError", "Could not load this resource."),
									)
						}
					/>
				) : (
					<CodeEditor
						inspect={true}
						language={resource.mediaType === "text/markdown" ? "markdown" : "plaintext"}
						wordWrap={true}
						height={240}
						value={content.data.content}
						aria-label={resource.name}
						data-testid={`skill-import-resource-content-${testIdSuffix}`}
					/>
				)
			) : null}
		</Stack>
	);
}
