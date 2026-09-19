import { Badge, Button, Group, Progress, Stack, Text } from "@mantine/core";
import { IconDownload, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import type { TranscriptionModelView } from "@/features/transcription/models/TranscriptionModels";

export interface TranscriptionModelRowProps {
	readonly model: TranscriptionModelView;
	readonly isSelected: boolean;
	readonly isRecommended: boolean;
	/** True while any mutation on THIS row is in flight; every action on the row is disabled meanwhile. */
	readonly isBusy: boolean;
	/** True while a mutation on another row is in flight: one transfer at a time reads better than a queue. */
	readonly isOtherBusy: boolean;
	readonly onDownload: (modelId: string) => void;
	readonly onCancel: (modelId: string) => void;
	readonly onSelect: (modelId: string) => void;
}

/**
 * One whisper catalogue row: what it costs on disk, whether this node has it, and the one action that row offers.
 *
 * A row is in exactly one state, so it renders exactly one action. `installed` is the disk fact and the download
 * phase is the transfer fact, and they are read in that order: a row whose weights are on disk is installed even if
 * the last transfer for it was cancelled, because a stale "cancelled" beside a usable model would tell the operator
 * to fetch something they already have.
 */
export function TranscriptionModelRow({
	model,
	isSelected,
	isRecommended,
	isBusy,
	isOtherBusy,
	onDownload,
	onCancel,
	onSelect,
}: TranscriptionModelRowProps) {
	const { t } = useTranslation();
	const isDownloading = !model.installed && model.downloadPhase === "running";
	const hasFailed = !model.installed && model.downloadPhase === "failed";
	const percent = model.downloadPercent === null ? null : Math.min(100, Math.round(model.downloadPercent));

	return (
		<Stack gap={4} data-testid={`transcription-model-row-${model.id}`}>
			<Group justify="space-between" wrap="nowrap" align="center">
				<Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
					<Text size="sm" fw={500} truncate={true}>
						{model.id}
					</Text>
					<Text size="xs" c="dimmed">
						{humanizeBytes(model.sizeBytes)}
					</Text>
					{isSelected ? (
						<Badge size="sm" variant="filled" data-testid={`transcription-model-selected-${model.id}`}>
							{t("pages.transcription.runtime.models.selected")}
						</Badge>
					) : null}
					{isRecommended ? (
						<Badge size="sm" variant="light" color="teal" data-testid={`transcription-model-recommended-${model.id}`}>
							{t("pages.transcription.runtime.models.recommended")}
						</Badge>
					) : null}
					{model.installed && !isSelected ? (
						<Badge size="sm" variant="light" color="gray">
							{t("pages.transcription.runtime.models.installed")}
						</Badge>
					) : null}
				</Group>
				{isDownloading ? (
					<Button
						size="xs"
						variant="light"
						color="red"
						leftSection={<IconX size={14} />}
						loading={isBusy}
						disabled={isBusy}
						onClick={() => onCancel(model.id)}
						data-testid={`transcription-model-cancel-${model.id}`}
					>
						{t("pages.transcription.runtime.models.cancel")}
					</Button>
				) : model.installed ? (
					isSelected ? null : (
						<Button
							size="xs"
							variant="light"
							loading={isBusy}
							disabled={isBusy || isOtherBusy}
							onClick={() => onSelect(model.id)}
							data-testid={`transcription-model-select-${model.id}`}
						>
							{t("pages.transcription.runtime.models.select")}
						</Button>
					)
				) : (
					<Button
						size="xs"
						variant="light"
						leftSection={<IconDownload size={14} />}
						loading={isBusy}
						disabled={isBusy || isOtherBusy}
						onClick={() => onDownload(model.id)}
						data-testid={`transcription-model-download-${model.id}`}
					>
						{hasFailed ? t("pages.transcription.runtime.models.retry") : t("pages.transcription.runtime.models.download")}
					</Button>
				)}
			</Group>
			{isDownloading ? (
				<>
					{/* No percentage is invented before the coordinator has declared a total: a bar that reads 100% while
					    bytes are still moving is how a stalled transfer gets reported as finished. */}
					<Progress
						value={percent ?? 100}
						striped={percent === null}
						animated={percent === null}
						size="sm"
						radius="sm"
						aria-label={t("pages.transcription.runtime.models.progressLabel")}
						data-testid={`transcription-model-progress-${model.id}`}
					/>
					<Text size="xs" c="dimmed">
						{percent === null
							? t("pages.transcription.runtime.models.starting")
							: t("pages.transcription.runtime.models.progress", { percent })}
					</Text>
				</>
			) : null}
			{hasFailed ? (
				<Text size="xs" c="red" data-testid={`transcription-model-failed-${model.id}`}>
					{model.downloadError === null
						? t("pages.transcription.runtime.models.downloadFailed")
						: t("pages.transcription.runtime.models.downloadFailedReason", { reason: model.downloadError })}
				</Text>
			) : null}
			{!model.installed && model.downloadPhase === "cancelled" ? (
				<Text size="xs" c="dimmed" data-testid={`transcription-model-cancelled-${model.id}`}>
					{t("pages.transcription.runtime.models.cancelled")}
				</Text>
			) : null}
		</Stack>
	);
}
