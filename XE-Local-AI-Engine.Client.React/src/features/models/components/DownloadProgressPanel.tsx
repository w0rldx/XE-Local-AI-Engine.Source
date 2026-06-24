import { Alert, Button, Card, Group, Loader, Progress, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { GgufDownloadStatus } from "@/features/models/queries/useGgufDownload";

interface DownloadProgressPanelProps {
	// Model names of in-flight GGUF downloads to surface.
	inFlight: readonly string[];
	// Backend status map keyed by modelName — provides byte progress + phase. Optional: falls back to
	// indeterminate when absent (e.g. before the first poll response arrives).
	downloadStatuses: ReadonlyMap<string, GgufDownloadStatus>;
	onCancel: (modelName: string) => void;
	cancellingModelName: string | null;
}

/** Formats a byte count into a human-readable string (e.g. "324 MB", "1.2 GB"). */
function humanizeBytes(bytes: number): string {
	if (bytes >= 1_073_741_824) {
		return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
	}
	if (bytes >= 1_048_576) {
		return `${Math.round(bytes / 1_048_576)} MB`;
	}
	if (bytes >= 1024) {
		return `${Math.round(bytes / 1024)} KB`;
	}
	return `${bytes} B`;
}

// In-flight GGUF download panel. Shows real byte-level progress when the backend reports Content-Length;
// falls back to an indeterminate loader when totalBytes is absent. Completed/Cancelled entries are dropped
// from the inFlight list by the store reconciliation in useActiveGgufDownloads. Failed entries show an
// error row with the sanitized backend message. Rendered only when there is at least one in-flight entry.
export function DownloadProgressPanel({
	inFlight,
	downloadStatuses,
	onCancel,
	cancellingModelName,
}: DownloadProgressPanelProps) {
	const { t } = useTranslation();

	if (inFlight.length === 0) {
		return null;
	}

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-download-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconCloudDownload size={20} />
					<Title order={4}>{t("pages.models.gguf.download.title", "Downloads in progress")}</Title>
				</Group>

				<Stack gap="sm">
					{inFlight.map((modelName) => {
						const status = downloadStatuses.get(modelName);
						const isFailed = status?.phase === "Failed";
						const pct = status?.pct;
						const hasDeterminate = pct !== undefined;

						return (
							<Stack key={modelName} gap="xs" data-testid={`model-fit-download-row-${modelName}`}>
								<Group justify="space-between" align="center">
									<Group gap="sm" align="center">
										{!hasDeterminate && !isFailed ? <Loader size="xs" /> : null}
										<Stack gap={0}>
											<Text size="sm" fw={500}>
												{modelName}
											</Text>
											{isFailed ? null : (
												<Text size="xs" c="dimmed">
													{hasDeterminate
														? t("pages.models.gguf.download.progressLabel", "{{pct}}%", { pct })
														: t("pages.models.gguf.download.inProgress", "Downloading…")}
												</Text>
											)}
										</Stack>
									</Group>
									<Group gap="xs" align="center">
										{hasDeterminate && status != null && status.completedBytes != null && status.totalBytes != null ? (
											<Text size="xs" c="dimmed">
												{t("pages.models.gguf.download.bytesOf", "{{completed}} / {{total}}", {
													completed: humanizeBytes(status.completedBytes),
													total: humanizeBytes(status.totalBytes),
												})}
											</Text>
										) : null}
										<Button
											size="xs"
											variant="light"
											color="red"
											leftSection={<IconX size={14} />}
											loading={cancellingModelName === modelName}
											disabled={cancellingModelName === modelName}
											onClick={() => onCancel(modelName)}
											data-testid={`model-fit-download-cancel-${modelName}`}
										>
											{t("pages.models.gguf.download.cancel", "Cancel")}
										</Button>
									</Group>
								</Group>

								{hasDeterminate ? (
									<Progress
										value={pct}
										aria-label={t("pages.models.gguf.download.progressAriaLabel", "Download progress")}
										size="sm"
										radius="sm"
									/>
								) : null}

								{isFailed && status?.sanitizedError ? (
									<Alert
										icon={<IconAlertTriangle size={16} />}
										color="red"
										variant="light"
										title={t("pages.models.gguf.download.failedTitle", "Download failed")}
									>
										{status.sanitizedError}
									</Alert>
								) : null}
							</Stack>
						);
					})}
				</Stack>
			</Stack>
		</Card>
	);
}
