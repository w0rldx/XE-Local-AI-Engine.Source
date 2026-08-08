import { Button, Group, Loader, Progress, Stack, Text } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ImageModelDownloadView } from "@/features/images/models/ImageModels";
import { formatDownloadEta, humanizeBytes } from "@/features/models/models/DownloadRateEstimate";

export interface DownloadRowProps {
	modelName: string;
	status: ImageModelDownloadView | undefined;
	etaSeconds: number | undefined;
	bytesPerSecond: number | undefined;
	isCancelling: boolean;
	onCancel: (modelName: string) => void;
}

// One in-flight file-set pull. A percentage is shown ONLY when the set total is known — every part must have declared a
// size for the backend to compute one — because a bar derived from a partial total would pass 100% and read as broken.
// Without a total the row still advances honestly: bytes transferred, the part being fetched, and the measured speed.
export function DownloadRow({ modelName, status, etaSeconds, bytesPerSecond, isCancelling, onCancel }: DownloadRowProps) {
	const { t } = useTranslation();

	const completedBytes = status?.completedBytes ?? null;
	const totalBytes = status?.totalBytes ?? null;
	const percent =
		totalBytes != null && totalBytes > 0 && completedBytes != null ? Math.min(100, Math.round((completedBytes / totalBytes) * 100)) : null;

	const partLabel =
		status?.partIndex != null && status.partCount != null && status.partCount > 1
			? t("pages.images.models.download.part", "Part {{index}} of {{count}}", { index: status.partIndex, count: status.partCount })
			: undefined;
	const speedLabel =
		bytesPerSecond !== undefined
			? t("pages.images.models.download.speed", "{{value}}/s", { value: humanizeBytes(bytesPerSecond) })
			: undefined;
	const etaDuration = formatDownloadEta(etaSeconds);
	const etaLabel = etaDuration ? t("pages.images.models.download.eta", "~{{duration}} left", { duration: etaDuration }) : undefined;

	// The headline: a percentage when one is computable, otherwise the bytes that HAVE moved. Never a fabricated
	// percentage — the operator reading "100%" on a bar that keeps moving is the failure this guards against.
	let headline = t("pages.images.models.download.starting", "Starting…");
	if (percent !== null) {
		headline = t("pages.images.models.download.progress", "Downloading… {{percent}}%", { percent });
	} else if (completedBytes != null) {
		headline = t("pages.images.models.download.transferred", "{{completed}} transferred", { completed: humanizeBytes(completedBytes) });
	}

	const bytesLabel =
		percent !== null && completedBytes != null && totalBytes != null
			? t("pages.images.models.download.bytesOf", "{{completed}} / {{total}}", {
					completed: humanizeBytes(completedBytes),
					total: humanizeBytes(totalBytes),
				})
			: undefined;

	return (
		<Stack gap={4} data-testid={`image-model-download-row-${modelName}`}>
			<Group justify="space-between" wrap="nowrap" align="center">
				<Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
					{percent === null ? <Loader size="xs" /> : null}
					<Text size="sm" fw={500} truncate={true}>
						{modelName}
					</Text>
				</Group>
				<Button
					size="xs"
					variant="light"
					color="red"
					leftSection={<IconX size={14} />}
					loading={isCancelling}
					disabled={isCancelling}
					onClick={() => onCancel(modelName)}
					data-testid={`image-model-download-cancel-${modelName}`}
				>
					{t("pages.images.models.download.cancel", "Cancel")}
				</Button>
			</Group>
			<Progress
				value={percent ?? 100}
				striped={percent === null}
				animated={percent === null}
				size="sm"
				radius="sm"
				aria-label={t("pages.images.models.download.progressAriaLabel", "Download progress")}
				data-testid={`image-model-download-bar-${modelName}`}
			/>
			<Text size="xs" c="dimmed" data-testid={`image-model-download-detail-${modelName}`}>
				{[headline, bytesLabel, partLabel, speedLabel, etaLabel].filter(Boolean).join(" · ")}
			</Text>
		</Stack>
	);
}
