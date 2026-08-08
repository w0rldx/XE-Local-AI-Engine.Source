import { Alert, Badge, Button, Card, Group, Loader, Stack, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCloudDownload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { catalogEntryRepoCount, type ImageModelCatalogEntryView, type ImageModelFitVerdict } from "@/features/images/models/ImageModels";
import { humanizeBytes } from "@/features/models/models/DownloadRateEstimate";

interface ImageModelCatalogPanelProps {
	entries: readonly ImageModelCatalogEntryView[];
	isLoading: boolean;
	error: unknown;
	// Entry ids whose install is either being submitted or already transferring, so those rows spin instead of
	// offering Install again. A second click is idempotent server-side, but a row that still says "Install" while its
	// own 9 GB download runs reads as "nothing happened".
	busyEntryIds: readonly string[];
	onInstall: (entry: ImageModelCatalogEntryView) => void;
}

// Mantine colour per fit verdict. Unknown is deliberately grey rather than green: the backend reports it when this
// box's memory budget could not be measured at all, and a green badge there would be a promise nobody checked.
const fitColors: Record<ImageModelFitVerdict, string> = {
	Fits: "green",
	Tight: "yellow",
	WontFit: "red",
	Unknown: "gray",
};

// English fallbacks for the fit badge and its explanation, so the label reads as a sentence even before i18n resolves
// (and so the enum name never leaks into the UI as a "label").
const fitLabels: Record<ImageModelFitVerdict, string> = {
	Fits: "Fits",
	Tight: "Tight fit",
	WontFit: "Too large",
	Unknown: "Fit unknown",
};

const fitHints: Record<ImageModelFitVerdict, string> = {
	Fits: "The weights that must stay resident ({{resident}}) fit inside the measured budget ({{budget}}).",
	Tight: "The resident weights ({{resident}}) only just fit inside the measured budget ({{budget}}). Expect it to be slow.",
	WontFit: "The resident weights ({{resident}}) exceed the measured budget ({{budget}}).",
	Unknown: "This machine's memory budget could not be measured, so no claim is made about whether this model runs.",
};

/**
 * The curated one-click install list — the answer to "it's very hard to select a model and fill out the form".
 *
 * Every row already carries the whole file-set (including the per-part repository overrides a cross-repo set like
 * Qwen-Image needs) plus verified sizes, so Install posts it unchanged and nothing is typed. The two annotations that
 * make the list actionable rather than decorative are computed server-side: whether the model is already installed,
 * and how its resident weights compare to this machine's measured memory budget.
 */
export function ImageModelCatalogPanel({ entries, isLoading, error, busyEntryIds, onInstall }: ImageModelCatalogPanelProps) {
	const { t } = useTranslation();

	if (error) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="image-model-catalog-error">
				{apiErrorMessage(error, t("pages.images.models.catalog.error", "Could not load the model catalog."))}
			</Alert>
		);
	}

	if (isLoading) {
		return <Loader size="sm" data-testid="image-model-catalog-loading" />;
	}

	if (entries.length === 0) {
		return (
			<Text c="dimmed" data-testid="image-model-catalog-empty">
				{t("pages.images.models.catalog.empty", "No catalog entries are available in this build.")}
			</Text>
		);
	}

	return (
		<Stack gap="sm" data-testid="image-model-catalog">
			<Text size="sm" c="dimmed">
				{t(
					"pages.images.models.catalog.description",
					"Verified model sets you can install with one click. Sizes and hardware fit are for this machine.",
				)}
			</Text>
			{entries.map((entry) => (
				<CatalogRow key={entry.id} entry={entry} isBusy={busyEntryIds.includes(entry.id)} onInstall={onInstall} />
			))}
		</Stack>
	);
}

interface CatalogRowProps {
	entry: ImageModelCatalogEntryView;
	isBusy: boolean;
	onInstall: (entry: ImageModelCatalogEntryView) => void;
}

function CatalogRow({ entry, isBusy, onInstall }: CatalogRowProps) {
	const { t } = useTranslation();

	const repoCount = catalogEntryRepoCount(entry);
	const fitHint = t(`pages.images.models.catalog.fit.${entry.fitVerdict}Hint`, fitHints[entry.fitVerdict], {
		resident: humanizeBytes(entry.residentBytes),
		budget: humanizeBytes(entry.fitBudgetBytes),
	});

	return (
		<Card withBorder={true} padding="sm" radius="sm" data-testid={`image-model-catalog-row-${entry.id}`}>
			<Stack gap="xs">
				<Group justify="space-between" wrap="nowrap" align="flex-start">
					<Stack gap={2} style={{ minWidth: 0 }}>
						<Group gap="xs" wrap="nowrap">
							<Text size="sm" fw={600} truncate={true}>
								{entry.displayName}
							</Text>
							{entry.recommended ? (
								<Badge color="blue" variant="light" size="sm" data-testid={`image-model-catalog-recommended-${entry.id}`}>
									{t("pages.images.models.catalog.recommended", "Recommended")}
								</Badge>
							) : null}
						</Group>
						<Text size="xs" c="dimmed" truncate={true}>
							{entry.repoId} · {entry.license}
						</Text>
					</Stack>
					{entry.isInstalled ? (
						<Badge color="green" variant="light" leftSection={<IconCheck size={12} />} data-testid={`image-model-catalog-installed-${entry.id}`}>
							{t("pages.images.models.catalog.installed", "Installed")}
						</Badge>
					) : (
						<Button
							size="xs"
							variant="light"
							leftSection={<IconCloudDownload size={14} />}
							loading={isBusy}
							disabled={isBusy}
							onClick={() => onInstall(entry)}
							data-testid={`image-model-catalog-install-${entry.id}`}
						>
							{t("pages.images.models.catalog.install", "Install")}
						</Button>
					)}
				</Group>

				<Group gap="xs" wrap="wrap">
					<Badge variant="default" size="sm">
						{humanizeBytes(entry.totalSizeBytes)}
					</Badge>
					<Badge variant="default" size="sm">
						{t("pages.images.models.catalog.parts", "{{count}} files", { count: entry.parts.length })}
					</Badge>
					<Badge variant="light">{t(`pages.images.models.families.${entry.family}`, entry.family)}</Badge>
					<Tooltip label={fitHint} multiline={true} maw={300}>
						<Badge color={fitColors[entry.fitVerdict]} variant="light" size="sm" data-testid={`image-model-catalog-fit-${entry.id}`}>
							{t(`pages.images.models.catalog.fit.${entry.fitVerdict}`, fitLabels[entry.fitVerdict])}
						</Badge>
					</Tooltip>
					{repoCount > 1 ? (
						<Badge color="grape" variant="light" size="sm" data-testid={`image-model-catalog-multirepo-${entry.id}`}>
							{t("pages.images.models.catalog.multiRepo", "Spans {{count}} repositories", { count: repoCount })}
						</Badge>
					) : null}
				</Group>

				{entry.notes ? (
					<Text size="xs" c="dimmed">
						{entry.notes}
					</Text>
				) : null}

				{entry.fitsOnDisk ? null : (
					<Alert
						variant="light"
						color="orange"
						icon={<IconAlertTriangle size={14} />}
						data-testid={`image-model-catalog-disk-${entry.id}`}
					>
						{t("pages.images.models.catalog.diskWarning", "Not enough free disk space for this download.")}
					</Alert>
				)}
			</Stack>
		</Card>
	);
}
