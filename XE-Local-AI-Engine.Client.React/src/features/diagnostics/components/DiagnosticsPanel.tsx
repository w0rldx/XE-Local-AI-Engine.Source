// Diagnostics panel.
//
// Lists locally-stored snapshots (newest first) and drills into a detail view. Header actions cover the session-only
// verbose-logging switch, importing a previously exported bundle, clearing all snapshots and opening a prefilled GitHub
// issue; each row can be viewed, exported or deleted. Export merges the node's scrubbed server bundle into the snapshot
// zip. Nothing leaves the machine: the issue button only opens a link, and the operator attaches the zip themselves.
// Below the list, the node's database snapshots (NodeBackupsCard) are taken and restored.

import { Alert, Badge, Button, FileButton, Group, Loader, Switch, Text, Tooltip } from "@mantine/core";
import {
	IconAlertTriangle,
	IconBrandGithub,
	IconDownload,
	IconEye,
	IconFileSearch,
	IconStethoscope,
	IconTrash,
	IconUpload,
} from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { Snapshot } from "@/core/diagnostics/Diagnostics";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { NodeBackupsCard } from "@/features/diagnostics/components/NodeBackupsCard";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { SnapshotDetail } from "@/features/diagnostics/components/SnapshotDetail";
import { buildIssueUrl } from "@/features/diagnostics/IssueUrl";
import { useLogLevel, useSetLogLevel } from "@/features/diagnostics/queries/useLogLevel";
import { useNodeInfo } from "@/features/diagnostics/queries/useNodeInfo";
import { exportSupportBundle } from "@/features/diagnostics/SupportBundle";
import { useClearSnapshots, useDeleteSnapshot, useImportSnapshot, useSnapshots } from "@/features/diagnostics/UseSnapshots";

function errorMessage(snapshot: Snapshot): string {
	return snapshot.error?.message ?? "—";
}

// The timestamp must not give up width (a wrapped date/time is unreadable); the row actions stay on one line in the
// table anyway. The error column is the one deliberately left wrapping, so it absorbs whatever width is left.
const nowrapCell = { whiteSpace: "nowrap" as const };

export function DiagnosticsPanel() {
	const { t } = useTranslation();
	const { data: snapshots, isLoading, isError } = useSnapshots();
	const deleteSnapshot = useDeleteSnapshot();
	const clearSnapshots = useClearSnapshots();
	const importSnapshot = useImportSnapshot();
	const exportBundle = useMutation({ mutationFn: exportSupportBundle });
	const { data: nodeInfo } = useNodeInfo();
	const { data: logLevel } = useLogLevel();
	const setLogLevel = useSetLogLevel();
	const { confirm } = useConfirm();
	const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

	const selected = snapshots?.find((snapshot) => snapshot.id === selectedId);
	const issueUrl = nodeInfo ? buildIssueUrl(nodeInfo, snapshots?.[0]) : undefined;

	const handleImport = (file: File | null): void => {
		if (!file) {
			return;
		}
		importSnapshot.mutate(file, {
			onSuccess: () => toast.success(t("diagnostics.importSuccess")),
			onError: () => toast.error(t("diagnostics.importError")),
		});
	};

	const handleClearAll = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("diagnostics.clearAll"),
			description: t("diagnostics.clearAllConfirm"),
		});
		if (confirmed) {
			clearSnapshots.mutate();
		}
	};

	if (selected) {
		return (
			<PageShell>
				<SnapshotDetail snapshot={selected} onBack={() => setSelectedId(undefined)} />
			</PageShell>
		);
	}

	return (
		<PageShell data-testid="diagnostics-page">
			<PageHeader
				title={t("diagnostics.title")}
				icon={<IconStethoscope size={24} />}
				subtitle={t("diagnostics.description")}
				actions={
					<>
						<Tooltip label={t("diagnostics.verboseLoggingHint")} multiline={true} w={280}>
							<Switch
								label={t("diagnostics.verboseLogging")}
								checked={logLevel?.verbose ?? false}
								disabled={!logLevel || setLogLevel.isPending}
								onChange={(event) =>
									setLogLevel.mutate(
										{ body: { verbose: event.currentTarget.checked } },
										{ onError: () => toast.error(t("diagnostics.verboseLoggingError")) },
									)
								}
							/>
						</Tooltip>
						<ReportProblemButton variant="button" />
						{issueUrl && (
							<Tooltip label={t("diagnostics.openIssueTooltip")} multiline={true} w={280}>
								<Button
									component="a"
									href={issueUrl}
									target="_blank"
									rel="noopener noreferrer"
									variant="default"
									leftSection={<IconBrandGithub size={16} />}
								>
									{t("diagnostics.openIssue")}
								</Button>
							</Tooltip>
						)}
						{/* The Button below IS the labelled control; FileButton's own input is display:none and only opens the
						    OS picker. Marked hidden so it is not reported as an unnamed form control. */}
						<FileButton onChange={handleImport} accept="application/zip,.zip" inputProps={{ "aria-hidden": true, tabIndex: -1 }}>
							{(props) => (
								<Button {...props} variant="default" leftSection={<IconUpload size={16} />} loading={importSnapshot.isPending}>
									{t("diagnostics.import")}
								</Button>
							)}
						</FileButton>
						<Button
							variant="default"
							color="red"
							leftSection={<IconTrash size={16} />}
							disabled={!snapshots || snapshots.length === 0}
							loading={clearSnapshots.isPending}
							onClick={handleClearAll}
						>
							{t("diagnostics.clearAll")}
						</Button>
					</>
				}
			/>

			<Alert variant="light" color="blue" icon={<IconAlertTriangle size={16} />}>
				{t("diagnostics.privacyNote")}
			</Alert>

			{isLoading && (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed" size="sm">
						{t("diagnostics.loading")}
					</Text>
				</Group>
			)}

			{isError && <InlineErrorAlert variant="light" message={t("diagnostics.loadError")} />}

			{!isLoading && !isError && snapshots && snapshots.length === 0 && (
				<EmptyState
					data-testid="diagnostics-empty"
					icon={<IconFileSearch size={40} opacity={0.6} />}
					message={
						<>
							<Text span={true} fw={600}>
								{t("diagnostics.empty.title")}
							</Text>
							<br />
							{t("diagnostics.empty.description")}
						</>
					}
				/>
			)}

			{!isLoading && !isError && snapshots && snapshots.length > 0 && (
				<SectionCard>
					{/*
					 * The three row actions are ~300px of buttons on their own, so the floor covers the actions column at
					 * its natural width (the table keeps it on one line); a narrower container gets cards instead.
					 */}
					<ResponsiveTable
						rows={snapshots}
						rowKey={(snapshot) => snapshot.id}
						minWidth={900}
						columns={[
							{
								key: "time",
								header: t("diagnostics.columns.time"),
								primary: true,
								style: nowrapCell,
								cell: (snapshot) => formatTimestamp(snapshot.createdAt),
							},
							{
								key: "kind",
								header: t("diagnostics.columns.kind"),
								cell: (snapshot) => (
									<Badge color={snapshot.kind === "error" ? "red" : "blue"} variant="light">
										{t(`diagnostics.kind.${snapshot.kind}`)}
									</Badge>
								),
							},
							{ key: "route", header: t("diagnostics.columns.route"), cell: (snapshot) => snapshot.env.route },
							{
								key: "error",
								header: t("diagnostics.columns.error"),
								style: { wordBreak: "break-word" },
								cell: (snapshot) => errorMessage(snapshot),
							},
						]}
						actionsHeader={t("diagnostics.columns.actions")}
						actions={(snapshot) => (
							<>
								<Button size="xs" variant="subtle" leftSection={<IconEye size={14} />} onClick={() => setSelectedId(snapshot.id)}>
									{t("diagnostics.actions.view")}
								</Button>
								<Button
									size="xs"
									variant="subtle"
									leftSection={<IconDownload size={14} />}
									loading={exportBundle.isPending && exportBundle.variables?.id === snapshot.id}
									onClick={() => exportBundle.mutate(snapshot)}
								>
									{t("diagnostics.actions.export")}
								</Button>
								<Button
									size="xs"
									variant="subtle"
									color="red"
									leftSection={<IconTrash size={14} />}
									loading={deleteSnapshot.isPending && deleteSnapshot.variables === snapshot.id}
									onClick={() => deleteSnapshot.mutate(snapshot.id)}
								>
									{t("diagnostics.actions.delete")}
								</Button>
							</>
						)}
					/>
				</SectionCard>
			)}

			<NodeBackupsCard />
		</PageShell>
	);
}
