import { ActionIcon, Alert, Badge, Card, Group, Loader, Stack, Tabs, Text, Tooltip } from "@mantine/core";
import { IconCloudDownload, IconPlus, IconSparkles, IconTrash } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import type { Dispatch, SetStateAction } from "react";

import { DownloadRow } from "@/features/images/components/ImageDownloadRow";
import { ManualDownloadForm } from "@/features/images/components/ImageManualDownloadForm";
import { type BrowseInstallRequest, ImageModelBrowsePanel } from "@/features/images/components/ImageModelBrowsePanel";
import { ImageModelCatalogPanel } from "@/features/images/components/ImageModelCatalogPanel";
import type {
	DownloadDraft,
	ImageModelCatalogEntryView,
	ImageModelDownloadView,
	ImageModelView,
	PartDraft,
} from "@/features/images/models/ImageModels";

interface InstalledModelsSectionProps {
	readonly models: readonly ImageModelView[];
	readonly formatBytes: (bytes: number) => string;
	readonly isLoading: boolean;
	readonly deletingModelName: string | null;
	readonly onDelete: (modelName: string) => void;
}

interface DownloadStatusSectionProps {
	readonly inFlight: readonly string[];
	readonly statuses: ReadonlyMap<string, ImageModelDownloadView>;
	readonly rateEstimates: ReadonlyMap<string, { readonly etaSeconds?: number; readonly bytesPerSecond?: number }>;
	readonly cancellingModelName: string | null;
	readonly onCancel: (modelName: string) => void;
	readonly errors: Readonly<Record<string, string>>;
	readonly onDismissError: (modelName: string) => void;
}

interface ImageModelEntrySectionProps {
	readonly catalogEntries: readonly ImageModelCatalogEntryView[];
	readonly catalogLoading: boolean;
	readonly catalogError: unknown;
	readonly busyCatalogIds: readonly string[];
	readonly onCatalogInstall: (entry: ImageModelCatalogEntryView) => void;
	readonly installedModelNames: readonly string[];
	readonly isInstalling: boolean;
	readonly onBrowseInstall: (request: BrowseInstallRequest) => void;
	readonly draft: DownloadDraft;
	readonly setDraft: Dispatch<SetStateAction<DownloadDraft>>;
	readonly advancedParts: readonly PartDraft[];
	readonly hasDiffusionPart: boolean;
	readonly canSubmit: boolean;
	readonly isDraftInFlight: boolean;
	readonly onSubmit: () => void;
	readonly onUpdatePart: (id: string, patch: Partial<PartDraft>) => void;
	readonly onAddPart: () => void;
	readonly onRemovePart: (id: string) => void;
}

interface ImageModelManagerPresentationProps {
	readonly t: TFunction;
	readonly installed: InstalledModelsSectionProps;
	readonly downloads: DownloadStatusSectionProps;
	readonly entries: ImageModelEntrySectionProps;
}

function InstalledModelsPanel({ t, installed }: Pick<ImageModelManagerPresentationProps, "t" | "installed">) {
	return (
		<Stack gap="xs">
			<Text fw={600}>{t("pages.images.models.installedTitle", "Installed image models")}</Text>
			{installed.isLoading ? (
				<Loader size="sm" data-testid="image-models-loading" />
			) : installed.models.length === 0 ? (
				<Text c="dimmed" data-testid="image-models-empty">
					{t("pages.images.models.empty", "No image models installed yet.")}
				</Text>
			) : (
				<Stack gap="xs" data-testid="image-models-list">
					{installed.models.map((model) => (
						<Card key={model.modelName} withBorder={true} padding="sm" radius="sm">
							<Group justify="space-between" wrap="nowrap">
								<Stack gap={2} style={{ minWidth: 0 }}>
									<Text size="sm" fw={500} truncate={true}>
										{model.modelName}
									</Text>
									<Text size="xs" c="dimmed" truncate={true}>
										{model.repoId}
									</Text>
								</Stack>
								<Group gap="xs" wrap="nowrap">
									<Text size="xs" c="dimmed">
										{installed.formatBytes(model.sizeBytes)}
									</Text>
									<Badge variant="light">{t(`pages.images.models.families.${model.family}`, model.family)}</Badge>
									<Tooltip label={t("pages.images.models.delete.action", "Delete model")}>
										<ActionIcon
											variant="light"
											color="red"
											aria-label={t("pages.images.models.delete.action", "Delete model")}
											loading={installed.deletingModelName === model.modelName}
											disabled={installed.deletingModelName === model.modelName}
											onClick={() => installed.onDelete(model.modelName)}
											data-testid={`image-model-delete-${model.modelName}`}
										>
											<IconTrash size={16} />
										</ActionIcon>
									</Tooltip>
								</Group>
							</Group>
						</Card>
					))}
				</Stack>
			)}
		</Stack>
	);
}

function DownloadStatusPanels({ t, downloads }: Pick<ImageModelManagerPresentationProps, "t" | "downloads">) {
	return (
		<>
			{downloads.inFlight.length > 0 ? (
				<Stack gap="sm" data-testid="image-model-download-progress">
					{downloads.inFlight.map((modelName) => (
						<DownloadRow
							key={modelName}
							modelName={modelName}
							status={downloads.statuses.get(modelName)}
							etaSeconds={downloads.rateEstimates.get(modelName)?.etaSeconds}
							bytesPerSecond={downloads.rateEstimates.get(modelName)?.bytesPerSecond}
							isCancelling={downloads.cancellingModelName === modelName}
							onCancel={downloads.onCancel}
						/>
					))}
				</Stack>
			) : null}
			{Object.entries(downloads.errors).map(([modelName, reason]) => (
				<Alert
					key={modelName}
					variant="light"
					color="red"
					withCloseButton={true}
					closeButtonLabel={t("pages.images.models.download.dismissError", "Dismiss")}
					onClose={() => downloads.onDismissError(modelName)}
					data-testid="image-model-download-error"
				>
					{reason}
				</Alert>
			))}
		</>
	);
}

function ImageModelEntryPanels({ t, entries }: Pick<ImageModelManagerPresentationProps, "t" | "entries">) {
	return (
		<Tabs defaultValue="catalog" keepMounted={false} data-testid="image-model-add-tabs">
			<Tabs.List>
				<Tabs.Tab value="catalog" leftSection={<IconSparkles size={14} />} data-testid="image-model-tab-catalog">
					{t("pages.images.models.tabs.catalog", "Recommended")}
				</Tabs.Tab>
				<Tabs.Tab value="browse" leftSection={<IconCloudDownload size={14} />} data-testid="image-model-tab-browse">
					{t("pages.images.models.tabs.browse", "Hugging Face")}
				</Tabs.Tab>
				<Tabs.Tab value="manual" leftSection={<IconPlus size={14} />} data-testid="image-model-tab-manual">
					{t("pages.images.models.tabs.manual", "Advanced")}
				</Tabs.Tab>
			</Tabs.List>
			<Tabs.Panel value="catalog" pt="md">
				<ImageModelCatalogPanel
					entries={entries.catalogEntries}
					isLoading={entries.catalogLoading}
					error={entries.catalogError}
					busyEntryIds={entries.busyCatalogIds}
					onInstall={entries.onCatalogInstall}
				/>
			</Tabs.Panel>
			<Tabs.Panel value="browse" pt="md">
				<ImageModelBrowsePanel
					installedModelNames={entries.installedModelNames}
					isInstalling={entries.isInstalling}
					onInstall={entries.onBrowseInstall}
				/>
			</Tabs.Panel>
			<Tabs.Panel value="manual" pt="md">
				<ManualDownloadForm
					draft={entries.draft}
					setDraft={entries.setDraft}
					advancedParts={entries.advancedParts}
					hasDiffusionPart={entries.hasDiffusionPart}
					canSubmit={entries.canSubmit}
					isDraftInFlight={entries.isDraftInFlight}
					isSubmitting={entries.isInstalling}
					onSubmit={entries.onSubmit}
					onUpdatePart={entries.onUpdatePart}
					onAddPart={entries.onAddPart}
					onRemovePart={entries.onRemovePart}
				/>
			</Tabs.Panel>
		</Tabs>
	);
}

export function ImageModelManagerPresentation(props: ImageModelManagerPresentationProps) {
	return (
		<Stack gap="md" data-testid="image-model-manager">
			<InstalledModelsPanel t={props.t} installed={props.installed} />
			<DownloadStatusPanels t={props.t} downloads={props.downloads} />
			<ImageModelEntryPanels t={props.t} entries={props.entries} />
		</Stack>
	);
}
