import { ActionIcon, Button, CopyButton, Group, Image, Loader, Stack, Text, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy, IconDownload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { buildGeneratedImageFileName, downloadGeneratedImage } from "@/features/images/GeneratedImageDownload";
import { useImageObjectUrl } from "@/features/images/hooks/useImageObjectUrl";
import type { ImageJobView } from "@/features/images/models/ImageModels";

interface ImageViewerDialogProps {
	job: ImageJobView;
	opened: boolean;
	onClose: () => void;
}

// One labelled metadata row. Left column is fixed-width so the values line up into a column of their own.
function MetadataRow({ label, children }: { label: string; children: React.ReactNode }) {
	return (
		<Group gap="sm" align="flex-start" wrap="nowrap">
			<Text size="xs" c="dimmed" w={110} style={{ flexShrink: 0 }}>
				{label}
			</Text>
			{children}
		</Group>
	);
}

/**
 * Full-size view of a generated image with its generation settings and a save-to-disk action.
 *
 * Sized deliberately wider than the DialogShell default (54rem): the point of this dialog is the image, and the shell's
 * ScrollArea.Autosize body would otherwise force a 1024px+ PNG into a nested scroll region. The image is capped by
 * viewport height rather than a fixed pixel box so it scales down to fit instead of overflowing, and the metadata sits
 * below it rather than beside it so neither competes with the other for width.
 */
export function ImageViewerDialog({ job, opened, onClose }: ImageViewerDialogProps) {
	const { t } = useTranslation();
	// Same query key as the inline thumbnail, so opening the dialog is a cache hit — no second fetch of the PNG.
	const { url, blob, isLoading, isError } = useImageObjectUrl(opened ? job.imageId : null);

	const handleDownload = (): void => {
		if (!blob) {
			return;
		}
		downloadGeneratedImage(blob, buildGeneratedImageFileName(job.modelName, job.seed));
	};

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.images.viewer.title", "Generated image")}
			size="min(80rem, 96vw)"
			data-testid="image-viewer-dialog"
			footer={
				<Button
					leftSection={<IconDownload size={16} />}
					onClick={handleDownload}
					disabled={!blob}
					data-testid="image-viewer-download"
				>
					{t("pages.images.viewer.download", "Download PNG")}
				</Button>
			}
		>
			<Stack gap="md">
				{isLoading ? <Loader size="sm" data-testid="image-viewer-loading" /> : null}

				{isError || (!isLoading && !url) ? (
					<Text size="sm" c="red" data-testid="image-viewer-error">
						{t("pages.images.result.error", "Could not load the generated image.")}
					</Text>
				) : null}

				{url ? (
					<Image
						src={url}
						alt={job.prompt}
						radius="sm"
						fit="contain"
						// `w="auto"` is load-bearing: Mantine's Image defaults to width 100%, which stretched a 512×512 PNG
						// to 1248px in this dialog (live-observed) — "full size" was upscaling and blurring it. Auto width
						// renders at natural size, and the two max-* caps scale it back down only when it would overflow.
						w="auto"
						mah="calc(100vh - 18rem)"
						maw="100%"
						mx="auto"
						data-testid="image-viewer-image"
					/>
				) : null}

				<Stack gap={6}>
					<MetadataRow label={t("pages.images.form.prompt.label", "Prompt")}>
						<Text size="sm" data-testid="image-viewer-prompt">
							{job.prompt}
						</Text>
					</MetadataRow>

					{job.negativePrompt ? (
						<MetadataRow label={t("pages.images.form.negativePrompt.label", "Negative prompt")}>
							<Text size="sm" data-testid="image-viewer-negative-prompt">
								{job.negativePrompt}
							</Text>
						</MetadataRow>
					) : null}

					<MetadataRow label={t("pages.images.viewer.seed", "Seed")}>
						{/*
						 * A negative seed means the job asked the runtime to pick one and the runtime never told us which.
						 * The pinned sd-server's job response carries no seed field at all (verified against the live
						 * daemon), so the value stays at the -1 sentinel. Showing "-1" next to a copy button would offer
						 * the operator a seed that reproduces nothing; say so instead until the stdout parser can
						 * recover the real one.
						 */}
						{job.seed < 0 ? (
							<Text size="sm" c="dimmed" data-testid="image-viewer-seed-random">
								{t("pages.images.viewer.seedRandom", "Random — not reported by the runtime")}
							</Text>
						) : (
							<Group gap={4} align="center">
								<Text size="sm" data-testid="image-viewer-seed">
									{job.seed}
								</Text>
								<CopyButton value={String(job.seed)} timeout={1500}>
									{({ copied, copy }) => (
										<Tooltip
											label={
												copied
													? t("pages.images.viewer.seedCopied", "Seed copied")
													: t("pages.images.viewer.copySeed", "Copy seed")
											}
											withArrow={true}
										>
											<ActionIcon
												size="sm"
												variant="subtle"
												color={copied ? "teal" : "gray"}
												onClick={copy}
												aria-label={t("pages.images.viewer.copySeed", "Copy seed")}
												data-testid="image-viewer-copy-seed"
											>
												{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
											</ActionIcon>
										</Tooltip>
									)}
								</CopyButton>
							</Group>
						)}
					</MetadataRow>

					<MetadataRow label={t("pages.images.viewer.settings", "Settings")}>
						<Text size="sm" data-testid="image-viewer-settings">
							{t(
								"pages.images.viewer.settingsValue",
								"{{model}} · {{width}}×{{height}} · {{steps}} steps · {{sampler}} · CFG {{cfgScale}}",
								{
									model: job.modelName,
									width: job.width,
									height: job.height,
									steps: job.steps,
									sampler: job.sampler,
									cfgScale: job.cfgScale,
								},
							)}
						</Text>
					</MetadataRow>

					{job.durationMs !== null ? (
						<MetadataRow label={t("pages.images.viewer.duration", "Duration")}>
							<Text size="sm" data-testid="image-viewer-duration">
								{t("pages.images.job.duration", "{{seconds}}s", { seconds: Math.round(job.durationMs / 1000) })}
							</Text>
						</MetadataRow>
					) : null}
				</Stack>
			</Stack>
		</DialogShell>
	);
}
