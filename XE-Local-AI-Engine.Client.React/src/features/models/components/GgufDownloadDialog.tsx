import { Alert, Badge, Button, Group, Loader, Radio, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload, IconStar } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { formatBytesAsGb } from "@/features/models/models/GgufFormatters";
import type { GgufFitVerdict, GgufQuantTier, GgufRepository, GgufRepositoryFile } from "@/features/models/models/GgufModels";
import { recommendedGgufFileName } from "@/features/models/models/GgufModels";
import { useInspectGgufRepository } from "@/features/models/queries/useGgufDownload";

// Mantine badge color per hardware fit verdict; Unknown is intentionally null (no probe → render a dimmed placeholder,
// not a misleading colored badge). Keys are exhaustive over GgufFitVerdict so a new verdict forces a compile update.
const fitVerdictColor: Record<GgufFitVerdict, string | null> = {
	Fits: "green",
	Tight: "yellow",
	WontFit: "red",
	Unknown: null,
};

// i18n suffix under `pages.models.gguf.download.fit.*` per verdict (Unknown has no label — it renders a dimmed dash).
const fitVerdictLabelKey: Record<Exclude<GgufFitVerdict, "Unknown">, string> = {
	Fits: "fits",
	Tight: "tight",
	WontFit: "wontFit",
};

// i18n suffix under `pages.models.gguf.download.quality.*` per static quality tier.
const qualityTierLabelKey: Record<GgufQuantTier, string> = {
	NearLossless: "nearLossless",
	SweetSpot: "sweetSpot",
	Balanced: "balanced",
	Small: "small",
	Minimal: "minimal",
};

interface GgufDownloadDialogProps {
	// The repo whose quants are being picked; null closes the dialog (and gates the inspect query).
	repository: GgufRepository | null;
	onClose: () => void;
	onConfirm: (repoId: string, file: GgufRepositoryFile) => void;
	// Fallback when inspection returns no files (e.g. discovery degraded/unreachable): download the default quant by
	// repo id only, preserving the pre-picker one-click capability so a degraded inspect never blocks downloading.
	onConfirmDefault: (repoId: string) => void;
	isDownloading: boolean;
}

// Quant picker dialog: on open it inspects the selected repo's .gguf files (quant + size, incl. Unsloth Dynamic UD-
// quants) and lets the operator pick exactly one to download. The chosen file's exact name is passed up so the
// backend resolves it verbatim — no re-matching, so a Dynamic quant like UD-Q4_K_XL downloads unambiguously.
export function GgufDownloadDialog({ repository, onClose, onConfirm, onConfirmDefault, isDownloading }: GgufDownloadDialogProps) {
	const { t } = useTranslation();
	const opened = repository !== null;
	const inspect = useInspectGgufRepository(repository?.repoId ?? "", opened);
	const files = useMemo<readonly GgufRepositoryFile[]>(() => inspect.data?.files ?? [], [inspect.data?.files]);
	// The operator's explicit pick, or null when they haven't chosen yet. The effective selection is DERIVED below so we
	// never store a value that the list no longer contains (avoids a derived-state effect): default to the backend's
	// recommended file (falling back to the first/smallest), but honor a still-present explicit choice.
	const [pickedFileName, setPickedFileName] = useState<string | null>(null);

	const selectedFileName =
		pickedFileName !== null && files.some((file) => file.fileName === pickedFileName)
			? pickedFileName
			: recommendedGgufFileName(files);

	const selectedFile = files.find((file) => file.fileName === selectedFileName) ?? null;

	const handleConfirm = (): void => {
		if (repository !== null && selectedFile !== null) {
			onConfirm(repository.repoId, selectedFile);
		}
	};

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.models.gguf.download.selectQuant", "Select a quant to download")}
			size="min(42rem, 95vw)"
			footer={
				<>
					<Button variant="default" onClick={onClose} data-testid="gguf-download-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconCloudDownload size={16} />}
						disabled={selectedFile === null}
						loading={isDownloading}
						onClick={handleConfirm}
						data-testid="gguf-download-confirm"
					>
						{t("pages.models.gguf.download.confirm", "Download")}
					</Button>
				</>
			}
		>
			<Stack gap="md" data-testid="gguf-download-dialog">
				<Text size="sm" c="dimmed">
					{repository?.repoId}
				</Text>

				{inspect.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.models.gguf.download.inspecting", "Loading quants…")}</Text>
					</Group>
				) : null}

				{inspect.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="gguf-download-error">
						{t("pages.models.gguf.download.inspectError", "Could not load this repository's files.")}
					</Alert>
				) : null}

				{!inspect.isLoading && !inspect.error && files.length === 0 ? (
					<Stack gap="sm" data-testid="gguf-download-empty">
						<Text c="dimmed">{t("pages.models.gguf.download.noFiles", "No downloadable GGUF files found.")}</Text>
						{repository !== null ? (
							<Button
								variant="light"
								leftSection={<IconCloudDownload size={16} />}
								loading={isDownloading}
								onClick={() => onConfirmDefault(repository.repoId)}
								data-testid="gguf-download-default"
							>
								{t("pages.models.gguf.download.defaultFallback", "Download default quant (Q4_K_M)")}
							</Button>
						) : null}
					</Stack>
				) : null}

				{files.length > 0 ? (
					<Radio.Group value={selectedFileName} onChange={setPickedFileName}>
						<Table verticalSpacing="sm" data-testid="gguf-download-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th />
									<Table.Th>{t("pages.models.gguf.download.columns.quant", "Quant")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.download.columns.size", "Size")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.download.columns.fit", "Fit")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{files.map((file) => {
									const fitColor = fitVerdictColor[file.fitVerdict];
									const fitLabelKey = file.fitVerdict === "Unknown" ? null : fitVerdictLabelKey[file.fitVerdict];
									return (
										<Table.Tr key={file.fileName} data-testid={`gguf-download-row-${file.quant}`}>
											<Table.Td>
												<Radio value={file.fileName} aria-label={file.quant} />
											</Table.Td>
											<Table.Td>
												<Stack gap={2}>
													<Group gap="xs" wrap="nowrap">
														<Text size="sm" fw={500}>
															{file.quant}
														</Text>
														{file.isDynamic ? (
															<Badge color="grape" variant="light" size="sm">
																{t("pages.models.gguf.download.dynamic", "Dynamic")}
															</Badge>
														) : null}
														{file.isRecommended ? (
															<Badge
																color="blue"
																variant="filled"
																size="sm"
																leftSection={<IconStar size={12} />}
																data-testid={`gguf-recommended-${file.quant}`}
															>
																{t("pages.models.gguf.download.recommended", "Recommended")}
															</Badge>
														) : null}
													</Group>
													<Text size="xs" c="dimmed">
														{t(`pages.models.gguf.download.quality.${qualityTierLabelKey[file.qualityTier]}`, file.qualityTier)}
													</Text>
												</Stack>
											</Table.Td>
											<Table.Td>{formatBytesAsGb(file.sizeBytes)}</Table.Td>
											<Table.Td>
												{fitColor !== null && fitLabelKey !== null ? (
													<Badge color={fitColor} variant="light" size="sm" data-testid={`gguf-fit-${file.quant}`}>
														{t(`pages.models.gguf.download.fit.${fitLabelKey}`, file.fitVerdict)}
													</Badge>
												) : (
													<Text size="sm" c="dimmed" data-testid={`gguf-fit-${file.quant}`}>
														—
													</Text>
												)}
											</Table.Td>
										</Table.Tr>
									);
								})}
							</Table.Tbody>
						</Table>
					</Radio.Group>
				) : null}
			</Stack>
		</DialogShell>
	);
}
