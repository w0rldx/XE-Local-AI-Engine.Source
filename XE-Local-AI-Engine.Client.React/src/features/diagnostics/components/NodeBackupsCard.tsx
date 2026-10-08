// Database snapshots card on the Diagnostics page.
//
// Lists the node's database snapshots and the outcome of this start's automatic pre-migration backup, takes a snapshot
// on demand and stages a restore. An accepted restore stops the node (it does not restart itself), so the card then
// shows a persistent notice and disables itself until the operator starts the app again.

import { Alert, Button, Table, Text } from "@mantine/core";
import { IconDatabase, IconHistory, IconPlayerStop } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { useCreateNodeBackup, useNodeBackups, useRestoreNodeBackup } from "@/features/diagnostics/queries/useNodeBackups";

const nowrapCell = { whiteSpace: "nowrap" as const };

export function NodeBackupsCard() {
	const { t } = useTranslation();
	const [stopping, setStopping] = useState(false);
	const { data, isError } = useNodeBackups(!stopping);
	const createBackup = useCreateNodeBackup();
	const restoreBackup = useRestoreNodeBackup();
	const { confirm } = useConfirm();

	const handleRestore = async (name: string): Promise<void> => {
		const confirmed = await confirm({
			title: t("diagnostics.backups.restoreConfirmTitle", { name }),
			description: t("diagnostics.backups.restoreConfirm"),
			confirmationText: t("diagnostics.backups.restoreConfirmAction"),
		});
		if (confirmed) {
			restoreBackup.mutate({ path: { name } }, { onSuccess: () => setStopping(true) });
		}
	};

	const automatic = data?.lastAutomaticBackup;
	const busy = stopping || createBackup.isPending || restoreBackup.isPending;

	return (
		<SectionCard
			title={t("diagnostics.backups.title")}
			icon={<IconDatabase size={22} />}
			data-testid="node-backups-card"
			actions={
				<Button
					variant="default"
					leftSection={<IconHistory size={16} />}
					disabled={busy || !data}
					loading={createBackup.isPending}
					onClick={() => createBackup.mutate({}, { onSuccess: () => toast.success(t("diagnostics.backups.takeSuccess")) })}
				>
					{t("diagnostics.backups.take")}
				</Button>
			}
		>
			<Text c="dimmed" size="sm">
				{t("diagnostics.backups.description")}
			</Text>

			{stopping && (
				<Alert variant="light" color="orange" icon={<IconPlayerStop size={16} />} data-testid="node-backups-stopping">
					{t("diagnostics.backups.stopping")}
				</Alert>
			)}

			{isError && !stopping && <InlineErrorAlert variant="light" message={t("diagnostics.backups.loadError")} />}

			{createBackup.isError && (
				<InlineErrorAlert variant="light" message={apiErrorMessage(createBackup.error, t("diagnostics.backups.takeError"))} />
			)}

			{restoreBackup.isError && (
				<InlineErrorAlert variant="light" message={apiErrorMessage(restoreBackup.error, t("diagnostics.backups.restoreError"))} />
			)}

			{automatic && (
				<Text size="sm">
					{t(`diagnostics.backups.automatic.${automatic.outcome}`, {
						time: formatTimestamp(automatic.atUtc),
						error: automatic.error ?? "—",
					})}
				</Text>
			)}

			{data && data.backups.length === 0 && (
				<Text c="dimmed" size="sm">
					{t("diagnostics.backups.empty")}
				</Text>
			)}

			{data && data.backups.length > 0 && (
				<Table.ScrollContainer minWidth={600}>
					<Table striped={true}>
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("diagnostics.backups.columns.name")}</Table.Th>
								<Table.Th style={nowrapCell}>{t("diagnostics.backups.columns.size")}</Table.Th>
								<Table.Th style={nowrapCell}>{t("diagnostics.backups.columns.created")}</Table.Th>
								<Table.Th style={nowrapCell}>{t("diagnostics.columns.actions")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{data.backups.map((backup) => (
								<Table.Tr key={backup.name}>
									<Table.Td style={{ wordBreak: "break-all" }}>{backup.name}</Table.Td>
									<Table.Td style={nowrapCell}>{humanizeBytes(backup.sizeBytes)}</Table.Td>
									<Table.Td style={nowrapCell}>{formatTimestamp(backup.createdUtc)}</Table.Td>
									<Table.Td style={nowrapCell}>
										<Button
											size="xs"
											variant="subtle"
											color="red"
											disabled={busy}
											loading={restoreBackup.isPending && restoreBackup.variables?.path.name === backup.name}
											onClick={() => handleRestore(backup.name)}
											aria-label={t("diagnostics.backups.restoreLabel", { name: backup.name })}
										>
											{t("diagnostics.backups.restore")}
										</Button>
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			)}
		</SectionCard>
	);
}
