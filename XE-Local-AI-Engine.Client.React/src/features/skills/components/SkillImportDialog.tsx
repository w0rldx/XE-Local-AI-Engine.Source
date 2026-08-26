import { Alert, Button, Group, Stack } from "@mantine/core";
import { IconAlertTriangle, IconDownload, IconSearch, IconX } from "@tabler/icons-react";
import { useCallback, useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse,
	XeLocalAiEngineClientServicesAgentsSkillImportConflictResolution,
} from "@/core/api/generated";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { SkillImportPreviewPresentation } from "@/features/skills/components/SkillImportPreviewPresentation";
import { type SkillImportSourceTab, SkillImportSourceTabs } from "@/features/skills/components/SkillImportSourceTabs";
import { useCommitSkillImport, usePreviewSkillImport } from "@/features/skills/queries/useSkillImport";

interface SkillImportDialogProps {
	opened: boolean;
	onClose: () => void;
}

/**
 * Two-step import of third-party skills: `preview` (writes nothing, returns a report) then `commit` (persists the
 * exact payload behind the report token). The report is the product — the operator approves what they were shown.
 *
 * The security posture is: **we refuse code, we show you everything, you decide.** Nothing here validates, scans or
 * sandboxes the content, and no copy may imply otherwise. The real gate is
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
			(report?.skills ?? [])
				.filter((candidate) => candidate.canImport && candidate.problems.length === 0)
				.map((candidate) => candidate.name),
		);
		return selected.filter((name) => importable.has(name));
	}, [report, selected]);
	const selectedSet = useMemo(() => new Set(selected), [selected]);
	const effectiveSelectionSet = useMemo(() => new Set(effectiveSelection), [effectiveSelection]);

	const handleCommit = useCallback(() => {
		if (!report || !acknowledged || effectiveSelection.length === 0) {
			return;
		}
		commit.mutate({ body: { acknowledged: true, conflictResolution, skillNames: [...effectiveSelection], token: report.token } });
	}, [acknowledged, commit, conflictResolution, effectiveSelection, report]);

	const canPreview =
		tab === "upload"
			? file !== null
			: tab === "github"
				? owner.trim().length > 0 && repository.trim().length > 0
				: markdown.trim().length > 0;
	const hasSelectedConflict = (report?.skills ?? []).some(
		(candidate) => candidate.conflictsWithExistingSkill && effectiveSelectionSet.has(candidate.name),
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
				<SkillImportPreviewPresentation
					acknowledged={acknowledged}
					conflictResolution={conflictResolution}
					hasSelectedConflict={hasSelectedConflict}
					onAcknowledgedChange={setAcknowledged}
					onConflictResolutionChange={setConflictResolution}
					onToggleCandidate={handleToggleCandidate}
					outcomes={outcomes}
					report={report}
					selected={selectedSet}
					selectedCount={effectiveSelection.length}
					warningId={warningId}
				/>

				{!report && !outcomes ? (
					<SkillImportSourceTabs
						tab={tab}
						file={file}
						owner={owner}
						repository={repository}
						markdown={markdown}
						onTabChange={setTab}
						onFileChange={setFile}
						onOwnerChange={setOwner}
						onRepositoryChange={setRepository}
						onMarkdownChange={setMarkdown}
					/>
				) : null}

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
