import { Alert, Badge, Button, Code, Group, List, Loader, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconFileDiff } from "@tabler/icons-react";
import { useCallback, useEffect } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	useApplyAgentHomePatch,
	usePreviewAgentHomePatch,
} from "@/core/ui/components/AgentHomePatchApplyDialog/useAgentHomePatch";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";

interface AgentHomePatchApplyDialogProps {
	runId: string;
	onClose: () => void;
}

/** One refusal: the node's own redacted reason, and the refused entry's folder-relative name when it has one. */
interface RejectionEntry {
	readonly reason: string;
	readonly path: string | null;
}

/**
 * Reads the node's per-error refusals off a ProblemDetails 409. The apply endpoint answers with the service's own
 * redacted rejection strings, one per error, and those are the whole content of the refusal — the top-level `detail`
 * is a generic sentence. Anything else (a network failure, a 500) has no list, and the caller falls back to
 * {@link apiErrorMessage}.
 *
 * The error `name` carries the refused entry when the node knew one. Its other two values name no entry and contain
 * no slash, which is what separates them from a folder-relative `<alias>/<rel>` path.
 */
function rejectionEntries(error: unknown): readonly RejectionEntry[] {
	if (!(error instanceof ApiError)) {
		return [];
	}

	const errors = (error.apiProblemDetails as unknown as Record<string, unknown>)["errors"];
	if (!Array.isArray(errors)) {
		return [];
	}

	return errors
		.map((entry) => (entry !== null && typeof entry === "object" ? (entry as Record<string, unknown>) : undefined))
		.map((entry) => {
			const reason = entry?.["reason"];
			const name = entry?.["name"];
			if (typeof reason !== "string" || reason.trim().length === 0) {
				return null;
			}
			return { reason, path: typeof name === "string" && name.includes("/") ? name : null };
		})
		.filter((entry): entry is RejectionEntry => entry !== null);
}

/** Splits refusals into the ones about a named file and the ones about the patch as a whole. */
function groupRejections(entries: readonly RejectionEntry[]) {
	const byPath = new Map<string, string[]>();
	const general: string[] = [];
	for (const entry of entries) {
		if (entry.path === null || entry.path.length === 0) {
			general.push(entry.reason);
			continue;
		}
		byPath.set(entry.path, [...(byPath.get(entry.path) ?? []), entry.reason]);
	}
	return { byPath: [...byPath.entries()], general };
}

/** Refusals, grouped under the file each one is about, with the patch-wide ones kept separate below them. */
function RejectionList({ entries }: { entries: readonly RejectionEntry[] }) {
	const { byPath, general } = groupRejections(entries);

	return (
		<Stack gap={6}>
			{byPath.map(([path, reasons]) => (
				<Stack key={path} gap={2}>
					<Code>{path}</Code>
					<List size="sm">
						{reasons.map((reason) => (
							<List.Item key={reason}>{reason}</List.Item>
						))}
					</List>
				</Stack>
			))}
			{general.length === 0 ? null : (
				<List size="sm">
					{general.map((reason) => (
						<List.Item key={reason}>{reason}</List.Item>
					))}
				</List>
			)}
		</Stack>
	);
}

/**
 * Review an AgentHome run's exported patch and, if the operator says so, land it on the host.
 *
 * This is the only surface in the product that writes to the operator's own folders on their behalf, so the order is
 * fixed: the preview runs first and Apply is disabled until the node says the patch checks clean. The Apply call
 * carries the hash the preview reported, which is what makes the approval an approval of THIS diff — a `changes.patch`
 * re-exported in between is refused by the node rather than landed. A failure leaves the dialog open with a re-read
 * button, because re-reading is exactly what the operator should do after a refusal.
 *
 * A per-file list, not a diff viewer: what an operator needs before approving is which files in which folder change
 * and by how much. Rendering hunks is a separate feature, and pretending to review content this list does not show
 * would be worse than showing the list honestly.
 */
export function AgentHomePatchApplyDialog({ runId, onClose }: AgentHomePatchApplyDialogProps) {
	const { t } = useTranslation();

	const preview = usePreviewAgentHomePatch();
	const apply = useApplyAgentHomePatch();

	const { mutate: runPreview, reset: resetPreview } = preview;
	const { reset: resetApply } = apply;

	const loadPreview = useCallback(() => {
		resetApply();
		runPreview({ path: { runId } });
	}, [resetApply, runId, runPreview]);

	// The dialog is mounted only while it is open, so this is the on-open read. Guarded on `isIdle` so a double-invoked
	// effect (React's development strict mode) does not fire a second `git apply --check` against the host folders.
	const isIdle = preview.isIdle;
	useEffect(() => {
		if (isIdle) {
			runPreview({ path: { runId } });
		}
	}, [isIdle, runId, runPreview]);

	const handleClose = useCallback(() => {
		resetPreview();
		resetApply();
		onClose();
	}, [onClose, resetApply, resetPreview]);

	const plan = preview.data;
	const applied = apply.data;
	// A plan that can be applied always carries the hash of what it validated; the null branch is the shapes that
	// never read a patch (over the size budget, unreadable), and those report `canApply: false` anyway.
	const previewedHash = plan?.canApply === true ? (plan.patchSha256 ?? null) : null;
	const canApply = previewedHash !== null && applied === undefined;

	const handleApply = useCallback(() => {
		if (previewedHash === null) {
			return;
		}
		apply.mutate({ body: { patchSha256: previewedHash }, path: { runId } });
	}, [apply, previewedHash, runId]);

	const applyRejections = rejectionEntries(apply.error);

	const dirtyStateLabel = useCallback(
		(state: string) => {
			if (state === "staged") {
				return t("chat.toolCall.patchApply.dirtyState.staged", "staged change");
			}
			if (state === "untracked") {
				return t("chat.toolCall.patchApply.dirtyState.untracked", "new file, not in git");
			}
			return t("chat.toolCall.patchApply.dirtyState.modified", "unsaved edit");
		},
		[t],
	);

	return (
		<DialogShell
			opened={true}
			onClose={handleClose}
			title={t("chat.toolCall.patchApply.title", "Review and apply changes")}
			size="xl"
			data-testid="agent-home-patch-apply-dialog"
			footer={
				<Group gap="sm">
					<Button variant="default" onClick={handleClose} data-testid="agent-home-patch-apply-close">
						{applied === undefined ? t("common.cancel", "Cancel") : t("common.close", "Close")}
					</Button>
					{applied === undefined ? (
						<>
							<Button
								variant="subtle"
								onClick={loadPreview}
								loading={preview.isPending}
								data-testid="agent-home-patch-apply-repreview"
							>
								{t("chat.toolCall.patchApply.recheck", "Check again")}
							</Button>
							<Button
								color="teal"
								leftSection={<IconCheck size={14} />}
								disabled={!canApply}
								loading={apply.isPending}
								onClick={handleApply}
								data-testid="agent-home-patch-apply-confirm"
							>
								{t("chat.toolCall.patchApply.apply", "Apply to my folders")}
							</Button>
						</>
					) : null}
				</Group>
			}
		>
			<Stack gap="sm">
				<Text size="sm" c="dimmed">
					{t(
						"chat.toolCall.patchApply.intro",
						"These changes were produced in a sandbox copy. Applying them writes to the folders on this computer.",
					)}
				</Text>
				<Group gap="xs">
					<Text size="sm" c="dimmed">
						{t("chat.toolCall.patchApply.runLabel", "Run")}
					</Text>
					<Code data-testid="agent-home-patch-apply-run">{runId}</Code>
				</Group>

				{preview.isPending ? (
					<Group gap="xs" data-testid="agent-home-patch-apply-loading">
						<Loader size="sm" />
						<Text size="sm">{t("chat.toolCall.patchApply.loading", "Checking the changes against your folders…")}</Text>
					</Group>
				) : null}

				{preview.error === null ? null : (
					<InlineErrorAlert
						message={apiErrorMessage(
							preview.error,
							t("chat.toolCall.patchApply.errors.preview", "Could not read the changes for this run."),
						)}
						data-testid="agent-home-patch-apply-preview-error"
					/>
				)}

				{plan === undefined ? null : (
					<>
						{plan.containsBinary ? (
							<Alert
								variant="light"
								color="yellow"
								icon={<IconAlertTriangle size={16} />}
								title={t("chat.toolCall.patchApply.binaryTitle", "Contains binary changes")}
								data-testid="agent-home-patch-apply-binary"
							>
								{t(
									"chat.toolCall.patchApply.binaryBody",
									"Binary files cannot be reviewed here, and this node does not apply them unless it has been configured to.",
								)}
							</Alert>
						) : null}

						{plan.dirtyTargets.length === 0 && !plan.dirtyCheckUnavailable ? null : (
							<Alert
								variant="light"
								color="yellow"
								role="status"
								icon={<IconAlertTriangle size={16} />}
								title={t("chat.toolCall.patchApply.dirtyTitle", "Some of these files have local changes")}
								data-testid="agent-home-patch-apply-dirty"
							>
								<Stack gap={4}>
									<Text size="sm">
										{t(
											"chat.toolCall.patchApply.dirtyBody",
											"These files already differ from your last commit. Applying writes over them, and may fail where the changes overlap.",
										)}
									</Text>
									{plan.dirtyTargets.length === 0 ? null : (
										<List size="sm">
											{plan.dirtyTargets.map((target) => (
												<List.Item key={target.path}>
													<Text size="sm" ff="monospace" span={true}>
														{target.path}
													</Text>
													{` — ${dirtyStateLabel(target.state)}`}
												</List.Item>
											))}
										</List>
									)}
									{plan.dirtyCheckUnavailable ? (
										<Text size="sm" c="dimmed">
											{t(
												"chat.toolCall.patchApply.dirtyUnavailable",
												"At least one folder's local state could not be read, so this list may be incomplete.",
											)}
										</Text>
									) : null}
								</Stack>
							</Alert>
						)}

						{plan.rejections.length === 0 ? null : (
							<InlineErrorAlert
								title={t("chat.toolCall.patchApply.rejectedTitle", "These changes cannot be applied")}
								message={<RejectionList entries={plan.rejections} />}
								data-testid="agent-home-patch-apply-rejections"
							/>
						)}

						{plan.files.length === 0 ? (
							<EmptyState
								message={t("chat.toolCall.patchApply.noFiles", "This run's patch changes no files.")}
								data-testid="agent-home-patch-apply-empty"
							/>
						) : (
							<Table
								striped={true}
								verticalSpacing="xs"
								aria-label={t("chat.toolCall.patchApply.tableLabel", "Files this patch changes")}
								data-testid="agent-home-patch-apply-files"
							>
								<Table.Thead>
									<Table.Tr>
										<Table.Th>{t("chat.toolCall.patchApply.columns.folder", "Folder")}</Table.Th>
										<Table.Th>{t("chat.toolCall.patchApply.columns.file", "File")}</Table.Th>
										<Table.Th>{t("chat.toolCall.patchApply.columns.change", "Change")}</Table.Th>
										<Table.Th>{t("chat.toolCall.patchApply.columns.lines", "Lines")}</Table.Th>
									</Table.Tr>
								</Table.Thead>
								<Table.Tbody>
									{plan.files.map((file) => (
										<Table.Tr key={`${file.alias}/${file.relativePath}`}>
											<Table.Td>
												<Code>{file.alias}</Code>
											</Table.Td>
											<Table.Td>
												<Text size="sm" ff="monospace">
													{file.relativePath}
												</Text>
											</Table.Td>
											<Table.Td>
												<Badge size="sm" variant="light" radius="sm">
													{file.changeType}
												</Badge>
											</Table.Td>
											<Table.Td>
												<Text size="sm" ff="monospace">
													{t("chat.toolCall.patchApply.lineCounts", "+{{added}} −{{removed}}", {
														added: file.added,
														removed: file.removed,
													})}
												</Text>
											</Table.Td>
										</Table.Tr>
									))}
								</Table.Tbody>
							</Table>
						)}
					</>
				)}

				{apply.error === null ? null : (
					<InlineErrorAlert
						title={t("chat.toolCall.patchApply.applyFailedTitle", "Nothing was applied")}
						message={
							<Stack gap={4}>
								{applyRejections.length === 0 ? (
									<Text size="sm">
										{apiErrorMessage(
											apply.error,
											t("chat.toolCall.patchApply.errors.apply", "The changes could not be applied."),
										)}
									</Text>
								) : (
									<RejectionList entries={applyRejections} />
								)}
								<Text size="sm" c="dimmed">
									{t(
										"chat.toolCall.patchApply.applyFailedHint",
										"Check again to read the current state of the changes before trying once more.",
									)}
								</Text>
							</Stack>
						}
						data-testid="agent-home-patch-apply-error"
					/>
				)}

				{applied === undefined ? null : (
					<Alert
						variant="light"
						color="teal"
						icon={<IconFileDiff size={16} />}
						title={t("chat.toolCall.patchApply.appliedTitle", "Applied to your folders")}
						data-testid="agent-home-patch-apply-applied"
					>
						<List size="sm">
							{applied.appliedFiles.map((file) => (
								<List.Item key={`${file.alias}/${file.relativePath}`}>
									{file.alias}/{file.relativePath}
								</List.Item>
							))}
						</List>
					</Alert>
				)}
			</Stack>
		</DialogShell>
	);
}
