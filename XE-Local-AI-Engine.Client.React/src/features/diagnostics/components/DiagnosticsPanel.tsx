// Diagnostics panel.
//
// Lists locally-stored snapshots (newest first) and drills into a detail view. Header actions cover
// importing a previously exported bundle and clearing all snapshots; each row can be viewed, exported
// or deleted. Everything is local-only — the data layer never transmits snapshot content.

import { Alert, Badge, Button, FileButton, Group, Loader, Table, Text } from "@mantine/core";
import {
	IconAlertTriangle,
	IconDownload,
	IconEye,
	IconFileSearch,
	IconStethoscope,
	IconTrash,
	IconUpload,
} from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { Snapshot } from "@/core/diagnostics/Diagnostics";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { SnapshotDetail } from "@/features/diagnostics/components/SnapshotDetail";
import { exportSnapshot } from "@/features/diagnostics/ExportSnapshot";
import { useClearSnapshots, useDeleteSnapshot, useImportSnapshot, useSnapshots } from "@/features/diagnostics/UseSnapshots";

function errorMessage(snapshot: Snapshot): string {
	return snapshot.error?.message ?? "—";
}

export function DiagnosticsPanel() {
	const { t } = useTranslation();
	const { data: snapshots, isLoading, isError } = useSnapshots();
	const deleteSnapshot = useDeleteSnapshot();
	const clearSnapshots = useClearSnapshots();
	const importSnapshot = useImportSnapshot();
	const { confirm } = useConfirm();
	const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

	const selected = snapshots?.find((snapshot) => snapshot.id === selectedId);

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
						<ReportProblemButton variant="button" />
						<FileButton onChange={handleImport} accept="application/zip,.zip">
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

			{isError && (
				<Alert variant="light" color="red" icon={<IconAlertTriangle size={16} />}>
					{t("diagnostics.loadError")}
				</Alert>
			)}

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
					<Table.ScrollContainer minWidth={720}>
						<Table striped={true} highlightOnHover={true}>
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("diagnostics.columns.time")}</Table.Th>
									<Table.Th>{t("diagnostics.columns.kind")}</Table.Th>
									<Table.Th>{t("diagnostics.columns.route")}</Table.Th>
									<Table.Th>{t("diagnostics.columns.error")}</Table.Th>
									<Table.Th>{t("diagnostics.columns.actions")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{snapshots.map((snapshot) => (
									<Table.Tr key={snapshot.id}>
										<Table.Td>{new Date(snapshot.createdAt).toLocaleString()}</Table.Td>
										<Table.Td>
											<Badge color={snapshot.kind === "error" ? "red" : "blue"} variant="light">
												{t(`diagnostics.kind.${snapshot.kind}`)}
											</Badge>
										</Table.Td>
										<Table.Td>{snapshot.env.route}</Table.Td>
										<Table.Td style={{ wordBreak: "break-word" }}>{errorMessage(snapshot)}</Table.Td>
										<Table.Td>
											<Group gap="xs" wrap="nowrap">
												<Button
													size="xs"
													variant="subtle"
													leftSection={<IconEye size={14} />}
													onClick={() => setSelectedId(snapshot.id)}
												>
													{t("diagnostics.actions.view")}
												</Button>
												<Button
													size="xs"
													variant="subtle"
													leftSection={<IconDownload size={14} />}
													onClick={() => exportSnapshot(snapshot)}
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
											</Group>
										</Table.Td>
									</Table.Tr>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				</SectionCard>
			)}
		</PageShell>
	);
}
