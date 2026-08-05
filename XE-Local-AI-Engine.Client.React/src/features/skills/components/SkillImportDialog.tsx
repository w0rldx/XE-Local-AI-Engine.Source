import { Alert, Badge, Button, Checkbox, FileInput, Group, List, Radio, Stack, Tabs, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconBrandGithub, IconClipboardText, IconDownload, IconSearch, IconUpload, IconX } from "@tabler/icons-react";
import { useCallback, useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type {
	XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse,
	XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution,
} from "@/core/api/generated";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { SkillImportCandidateCard } from "@/features/skills/components/SkillImportCandidateCard";
import { useCommitSkillImport, usePreviewSkillImport } from "@/features/skills/queries/useSkillImport";

interface SkillImportDialogProps {
	opened: boolean;
	onClose: () => void;
}

type SkillImportSourceTab = "upload" | "github" | "paste";

// Untranslated on purpose: it is a SKILL.md skeleton, and `name`/`description` are literal frontmatter keys.
const PASTE_PLACEHOLDER = "---\nname: invoice-review\ndescription: …\n---\n\n# …";

/**
 * Two-step import of third-party skills: `preview` (writes nothing, returns a report) then `commit` (persists the
 * exact payload behind the report token). The report is the product — the operator approves what they were shown.
 *
 * The security posture this dialog implements, verbatim from the plan: **we refuse code, we show you everything, you
 * decide.** Nothing here validates, scans or sandboxes the content, and no copy may imply otherwise. The real gate is
 * that imported skills land DISABLED; the acknowledgement checkbox is a speed bump, not a control.
 */
export function SkillImportDialog({ opened, onClose }: SkillImportDialogProps) {
	const { t } = useTranslation();
	const warningId = useId();

	const [tab, setTab] = useState<SkillImportSourceTab>("upload");
	const [file, setFile] = useState<File | null>(null);
	const [owner, setOwner] = useState("");
	const [repository, setRepository] = useState("");
	const [markdown, setMarkdown] = useState("");

	const [report, setReport] = useState<XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse | null>(null);
	const [selected, setSelected] = useState<readonly string[]>([]);
	const [acknowledged, setAcknowledged] = useState(false);
	// Skip is the default on purpose: Replace overwrites an existing skill and loses local edits, and silently
	// destroying operator content is the worst available default.
	const [conflictResolution, setConflictResolution] =
		useState<XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution>("Skip");

	const preview = usePreviewSkillImport();
	const commit = useCommitSkillImport();

	// Full reset so a reopened dialog never shows a stale report, selection or acknowledgement from a previous import.
	const handleClose = useCallback(() => {
		setReport(null);
		setSelected([]);
		setAcknowledged(false);
		setConflictResolution("Skip");
		setFile(null);
		setOwner("");
		setRepository("");
		setMarkdown("");
		preview.reset();
		commit.reset();
		onClose();
	}, [commit, onClose, preview]);

	const handlePreview = useCallback(() => {
		const body =
			tab === "upload"
				? ({ source: "Upload", file } as const)
				: tab === "github"
					? ({ source: "GitHub", owner: owner.trim(), repository: repository.trim() } as const)
					: ({ source: "Paste", markdown } as const);

		preview.mutate(
			{ body },
			{
				onSuccess: (data) => {
					// A fresh report invalidates every earlier choice: the names, conflicts and problems all changed.
					setReport(data);
					setSelected([]);
					setAcknowledged(false);
					setConflictResolution("Skip");
					commit.reset();
				},
			},
		);
	}, [commit, file, markdown, owner, preview, repository, tab]);

	const handleToggleCandidate = useCallback((name: string, isSelected: boolean) => {
		setSelected((current) => (isSelected ? [...current, name] : current.filter((entry) => entry !== name)));
	}, []);

	// The selection that actually counts. A candidate carrying a problem can never be part of it, whatever the DOM says:
	// the checkbox being disabled is a UI affordance, this is the invariant. It also drops a stale name if a re-preview
	// turned a previously importable candidate into a broken one.
	const effectiveSelection = useMemo(() => {
		const importable = new Set(
			(report?.skills ?? []).filter((candidate) => candidate.canImport && candidate.problems.length === 0).map((candidate) => candidate.name),
		);
		return selected.filter((name) => importable.has(name));
	}, [report, selected]);

	const handleCommit = useCallback(() => {
		if (!report || !acknowledged || effectiveSelection.length === 0) {
			return;
		}
		commit.mutate({ body: { acknowledged: true, conflictResolution, skillNames: [...effectiveSelection], token: report.token } });
	}, [acknowledged, commit, conflictResolution, effectiveSelection, report]);

	const canPreview =
		tab === "upload" ? file !== null : tab === "github" ? owner.trim().length > 0 && repository.trim().length > 0 : markdown.trim().length > 0;
	const hasSelectedConflict = (report?.skills ?? []).some(
		(candidate) => candidate.conflictsWithExistingSkill && effectiveSelection.includes(candidate.name),
	);
	const outcomes = commit.data?.outcomes ?? null;

	return (
		<DialogShell
			opened={opened}
			onClose={handleClose}
			title={t("pages.skills.import.title", "Import skills")}
			zIndex={300}
			data-testid="skill-import-dialog"
			footer={
				outcomes ? (
					<Button onClick={handleClose} data-testid="skill-import-done">
						{t("common.close", "Close")}
					</Button>
				) : (
					<>
						<Button variant="subtle" leftSection={<IconX size={16} />} onClick={handleClose} data-testid="skill-import-cancel">
							{t("common.cancel", "Cancel")}
						</Button>
						{report ? (
							<Button
								leftSection={<IconDownload size={16} />}
								onClick={handleCommit}
								loading={commit.isPending}
								disabled={!acknowledged || effectiveSelection.length === 0}
								data-testid="skill-import-submit"
							>
								{t("pages.skills.import.importButton", "Import {{count}} selected", { count: effectiveSelection.length })}
							</Button>
						) : (
							<Button
								leftSection={<IconSearch size={16} />}
								onClick={handlePreview}
								loading={preview.isPending}
								disabled={!canPreview}
								data-testid="skill-import-preview"
							>
								{t("pages.skills.import.previewButton", "Preview")}
							</Button>
						)}
					</>
				)
			}
		>
			<Stack gap="md" px="md" pb="md">
				<Alert
					id={warningId}
					color="red"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					title={t("pages.skills.import.warning.title", "Imported skills are untrusted content")}
					data-testid="skill-import-warning"
				>
					<Stack gap={6}>
						<Text size="sm">
							{t(
								"pages.skills.import.warning.untrusted",
								"Skills you import are third-party instructions. This node does not validate, scan or sandbox them. A skill's body is injected verbatim into your agent's context and can attempt to redirect the agent to do something other than what its description says.",
							)}
						</Text>
						<Text size="sm" fw={700} data-testid="skill-import-warning-consequence">
							{t(
								"pages.skills.import.warning.consequence",
								"An enabled skill's instructions run with your agent's tool access — including reading your knowledge base and local workspace files without a further prompt.",
							)}
						</Text>
						<Text size="sm">
							{t(
								"pages.skills.import.warning.posture",
								"Scripts are never imported. Everything else is shown to you exactly as it will be stored — read the full body and every resource below, then decide.",
							)}
						</Text>
						<Text size="sm">
							{t(
								"pages.skills.import.warning.disabled",
								"Imported skills arrive disabled. Enabling one is a separate, deliberate step.",
							)}
						</Text>
					</Stack>
				</Alert>

				<Checkbox
					checked={acknowledged}
					onChange={(event) => setAcknowledged(event.currentTarget.checked)}
					aria-describedby={warningId}
					data-testid="skill-import-acknowledge"
					label={t(
						"pages.skills.import.acknowledge",
						"I understand this content is untrusted and that I am responsible for reviewing it.",
					)}
				/>

				{outcomes ? (
					<Stack gap="xs" data-testid="skill-import-outcomes">
						<Text fw={600}>{t("pages.skills.import.outcomes.title", "Import result")}</Text>
						<List size="sm" withPadding={true}>
							{outcomes.map((outcome) => (
								<List.Item key={outcome.name} data-testid={`skill-import-outcome-${outcome.name}`}>
									<Text component="span" ff="monospace" size="sm">
										{outcome.name}
									</Text>
									<Badge ml="xs" size="sm" variant="light" color={outcome.status === "Skipped" ? "gray" : "teal"}>
										{outcome.status}
									</Badge>
									{outcome.reason ? (
										<Text component="span" size="sm" c="dimmed">
											{` — ${outcome.reason}`}
										</Text>
									) : null}
								</List.Item>
							))}
						</List>
						<Text size="sm" c="dimmed">
							{t(
								"pages.skills.import.outcomes.disabledNote",
								"Imported skills are disabled. Open one to review it, then enable it deliberately.",
							)}
						</Text>
					</Stack>
				) : report ? (
					<Stack gap="md" data-testid="skill-import-report">
						<Text size="sm" c="dimmed">
							{t("pages.skills.import.report.summary", "Source {{source}} · {{found}} skills found · {{selected}} selected", {
								found: report.skills.length,
								selected: effectiveSelection.length,
								source: report.sourceUri,
							})}
						</Text>
						{report.warnings.length > 0 ? (
							<Alert color="blue" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="skill-import-report-warnings">
								<List size="sm" withPadding={true}>
									{report.warnings.map((warning) => (
										<List.Item key={warning}>{warning}</List.Item>
									))}
								</List>
							</Alert>
						) : null}
						{hasSelectedConflict ? (
							<Radio.Group
								value={conflictResolution}
								onChange={(value) =>
									setConflictResolution(value as XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution)
								}
								label={t("pages.skills.import.conflict.label", "A selected skill already exists on this node")}
								data-testid="skill-import-conflict-resolution"
							>
								<Stack gap={4} mt={4}>
									<Radio
										value="Skip"
										data-testid="skill-import-conflict-skip"
										label={t("pages.skills.import.conflict.skip", "Skip it and keep what is on this node")}
									/>
									<Radio
										value="Replace"
										data-testid="skill-import-conflict-replace"
										label={t("pages.skills.import.conflict.replace", "Replace it — this overwrites the existing skill and loses any local edits")}
									/>
								</Stack>
							</Radio.Group>
						) : null}
						{report.skills.length === 0 ? (
							<Text c="dimmed" data-testid="skill-import-report-empty">
								{t("pages.skills.import.report.empty", "This source contains no skills.")}
							</Text>
						) : null}
						{report.skills.map((candidate) => (
							<SkillImportCandidateCard
								key={candidate.name}
								candidate={candidate}
								selected={selected.includes(candidate.name)}
								onToggle={handleToggleCandidate}
							/>
						))}
					</Stack>
				) : (
					<Tabs value={tab} onChange={(value) => setTab((value ?? "upload") as SkillImportSourceTab)} keepMounted={false}>
						<Tabs.List>
							<Tabs.Tab value="upload" leftSection={<IconUpload size={14} />} data-testid="skill-import-tab-upload">
								{t("pages.skills.import.tabs.upload", "Upload")}
							</Tabs.Tab>
							<Tabs.Tab value="github" leftSection={<IconBrandGithub size={14} />} data-testid="skill-import-tab-github">
								{t("pages.skills.import.tabs.github", "GitHub")}
							</Tabs.Tab>
							<Tabs.Tab value="paste" leftSection={<IconClipboardText size={14} />} data-testid="skill-import-tab-paste">
								{t("pages.skills.import.tabs.paste", "Paste")}
							</Tabs.Tab>
						</Tabs.List>

						<Tabs.Panel value="upload" pt="md">
							<FileInput
								value={file}
								onChange={setFile}
								accept=".zip,application/zip"
								clearable={true}
								label={t("pages.skills.import.upload.label", "Skill archive (.zip)")}
								description={t(
									"pages.skills.import.upload.description",
									"A .zip containing one or more SKILL.md files with their bundled resources.",
								)}
								placeholder={t("pages.skills.import.upload.placeholder", "Choose a .zip file")}
								data-testid="skill-import-file"
							/>
						</Tabs.Panel>

						<Tabs.Panel value="github" pt="md">
							<Stack gap="sm">
								<Text size="sm" c="dimmed">
									{t(
										"pages.skills.import.github.description",
										"Only github.com is reachable, and only by owner and repository — a pasted URL is never accepted.",
									)}
								</Text>
								<TextInput
									value={owner}
									onChange={(event) => setOwner(event.currentTarget.value)}
									label={t("pages.skills.import.github.owner", "Owner")}
									placeholder="microsoft"
									data-testid="skill-import-owner"
								/>
								<TextInput
									value={repository}
									onChange={(event) => setRepository(event.currentTarget.value)}
									label={t("pages.skills.import.github.repository", "Repository")}
									placeholder="skills"
									data-testid="skill-import-repository"
								/>
							</Stack>
						</Tabs.Panel>

						<Tabs.Panel value="paste" pt="md">
							<Textarea
								value={markdown}
								onChange={(event) => setMarkdown(event.currentTarget.value)}
								autosize={true}
								minRows={8}
								maxRows={20}
								label={t("pages.skills.import.paste.label", "SKILL.md")}
								description={t(
									"pages.skills.import.paste.description",
									"Paste a complete SKILL.md, including its YAML frontmatter.",
								)}
								placeholder={PASTE_PLACEHOLDER}
								data-testid="skill-import-markdown"
							/>
						</Tabs.Panel>
					</Tabs>
				)}

				{preview.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="skill-import-preview-error">
						{apiErrorMessage(preview.error, t("pages.skills.import.errors.preview", "Could not read that source."))}
					</Alert>
				) : null}
				{commit.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="skill-import-commit-error">
						{apiErrorMessage(commit.error, t("pages.skills.import.errors.commit", "Could not import the selected skills."))}
					</Alert>
				) : null}

				{report && !outcomes ? (
					<Group justify="flex-start">
						<Button
							variant="subtle"
							size="compact-sm"
							onClick={() => {
								setReport(null);
								setSelected([]);
								preview.reset();
							}}
							data-testid="skill-import-back"
						>
							{t("pages.skills.import.back", "Choose another source")}
						</Button>
					</Group>
				) : null}
			</Stack>
		</DialogShell>
	);
}
