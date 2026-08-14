import { Alert, Button, Card, Group, Loader, Progress, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconFileImport, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { humanizeBytes } from "@/features/models/models/DownloadRateEstimate";
import type { GgufAcquisitionStatus } from "@/features/models/models/GgufAcquisitionModels";
import { isCancellableAcquisitionPhase } from "@/features/models/models/GgufAcquisitionModels";
import { importErrorMessage } from "@/features/models/models/GgufImportErrors";

interface ImportProgressPanelProps {
	readonly operations: readonly GgufAcquisitionStatus[];
	readonly onCancel: (operationId: string) => void;
	readonly cancellingOperationId: string | null;
}

export function ImportProgressPanel({ operations, onCancel, cancellingOperationId }: ImportProgressPanelProps) {
	const { t } = useTranslation();
	if (operations.length === 0) {
		return null;
	}
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-import-operations">
			<Stack gap="md" aria-live="polite" aria-label={t("pages.models.gguf.import.progressRegion", "Model import status")}>
				<Group gap="xs"><IconFileImport size={20} /><Title order={4}>{t("pages.models.gguf.import.progressTitle", "Model imports")}</Title></Group>
				{operations.map((status) => (
					<Stack key={status.operationId} gap="xs" data-testid={`model-import-operation-${status.operationId}`}>
						<Group justify="space-between" align="center">
							<Group gap="sm">
								{isCancellableAcquisitionPhase(status.phase) && status.pct === undefined ? <Loader size="xs" /> : null}
								<Stack gap={0}>
									<Text fw={500}>{status.modelName}</Text>
									<Text size="xs" c="dimmed">{t(`pages.models.gguf.import.phases.${status.phase}`, status.phase)}</Text>
								</Stack>
							</Group>
							{isCancellableAcquisitionPhase(status.phase) ? (
								<Button
									size="xs"
									variant="light"
									color="red"
									leftSection={<IconX size={14} />}
									loading={cancellingOperationId === status.operationId}
									onClick={() => onCancel(status.operationId)}
								>
									{t("pages.models.gguf.import.cancel", "Cancel import")}
								</Button>
							) : null}
						</Group>
						{status.pct !== undefined ? <Progress value={status.pct} aria-label={t("pages.models.gguf.import.progressAria", "Import progress")} /> : null}
						{status.completedBytes != null && status.totalBytes != null ? (
							<Text size="xs" c="dimmed">{humanizeBytes(status.completedBytes)} / {humanizeBytes(status.totalBytes)}</Text>
						) : null}
						{status.phase === "Failed" ? (
							<Alert icon={<IconAlertTriangle size={16} />} color="red" title={t("pages.models.gguf.import.failed", "Import failed")}>
								{importErrorMessage(t, status.errorCode)}
							</Alert>
						) : null}
					</Stack>
				))}
			</Stack>
		</Card>
	);
}
