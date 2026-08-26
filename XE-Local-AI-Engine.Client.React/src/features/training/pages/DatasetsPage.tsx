import { Badge, Button, Code, Group, Loader, Progress, ScrollArea, Stack, Tabs, Text, UnstyledButton } from "@mantine/core";
import { IconDatabase, IconDownload, IconPencil, IconPlayerPlay, IconPlus, IconShieldCheck, IconTrash } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { DatasetSampleReview } from "@/features/training/components/DatasetSampleReview";
import { DefinitionEditorDialog } from "@/features/training/components/DefinitionEditorDialog";
import { useDatasetGenerationHub } from "@/features/training/hooks/useDatasetGenerationHub";
import type { TrainingDataset, TrainingDefinition } from "@/features/training/models/TrainingModels";
import { isDatasetGenerationCancellable } from "@/features/training/models/TrainingModels";
import {
	useCancelTrainingDataset,
	useDeleteTrainingDataset,
	useDeleteTrainingDefinition,
	useExportTrainingDataset,
	useGenerateDataset,
	useToolMocks,
	useTrainingDatasets,
	useTrainingDefinitions,
	useVerifyToolMock,
} from "@/features/training/queries/useTrainingDatasets";

type DatasetsTab = "definitions" | "datasets" | "mocks";

/**
 * The dataset module surface: definitions (what to generate), datasets (what was generated, with sample review) and
 * tool mocks (what a generated tool call is allowed to hit).
 */
export function DatasetsPage() {
	const { t } = useTranslation();
	const [tab, setTab] = useState<DatasetsTab>("datasets");
	const [selectedDatasetId, setSelectedDatasetId] = useState<string | null>(null);
	const [exported, setExported] = useState<{ format: string; content: string } | null>(null);
	// `undefined` = editor closed; `null` = creating; a definition = editing it.
	const [editing, setEditing] = useState<TrainingDefinition | null | undefined>(undefined);

	const definitionsQuery = useTrainingDefinitions();
	const datasetsQuery = useTrainingDatasets();
	const mocksQuery = useToolMocks();
	const generate = useGenerateDataset();
	const deleteDefinition = useDeleteTrainingDefinition();
	const deleteDataset = useDeleteTrainingDataset();
	const cancelDataset = useCancelTrainingDataset();
	const verifyMock = useVerifyToolMock();
	const exportDataset = useExportTrainingDataset();
	const requestFailed = (error: unknown): void =>
		toast.error(apiErrorMessage(error, t("training.errors.request", "The training request failed.")));

	const datasets = datasetsQuery.data ?? [];
	const selected = useMemo(() => datasets.find((dataset) => dataset.id === selectedDatasetId) ?? null, [datasets, selectedDatasetId]);
	const generatingDataset = useMemo(() => datasets.find((dataset) => dataset.status === "Generating") ?? null, [datasets]);

	const refetchDatasets = datasetsQuery.refetch;
	const resync = useCallback(() => {
		refetchDatasets().catch(() => undefined);
	}, [refetchDatasets]);
	const progress = useDatasetGenerationHub(generatingDataset?.id ?? null, resync);

	return (
		<PageShell>
			<PageHeader
				title={t("pages.training.datasets.title", "Training datasets")}
				icon={<IconDatabase size={24} />}
				subtitle={t(
					"pages.training.datasets.subtitle",
					"Generate tool-calling datasets from a definition, review every sample, and export them for a training run.",
				)}
				data-tour="training-datasets-overview"
			/>

			{generatingDataset ? (
				<SectionCard gap="xs" title={t("training.generation.title", "Generation in progress")}>
					<Text size="sm">{generatingDataset.name}</Text>
					<Progress
						value={progress.total > 0 ? (progress.completed / progress.total) * 100 : 0}
						striped={true}
						animated={true}
						data-testid="training-generation-progress"
					/>
					<Text size="xs" c="dimmed">
						{t("training.generation.counts", "{{completed}} of {{total}} samples · {{rejected}} rejected", {
							completed: progress.completed,
							total: progress.total,
							rejected: progress.rejected,
						})}
					</Text>
				</SectionCard>
			) : null}

			<Tabs value={tab} onChange={(value) => setTab((value ?? "datasets") as DatasetsTab)}>
				<Tabs.List>
					<Tabs.Tab value="definitions" data-testid="training-tab-definitions">
						{t("training.tabs.definitions", "Definitions")}
					</Tabs.Tab>
					<Tabs.Tab value="datasets" data-testid="training-tab-datasets">
						{t("training.tabs.datasets", "Datasets")}
					</Tabs.Tab>
					<Tabs.Tab value="mocks" data-testid="training-tab-mocks">
						{t("training.tabs.mocks", "Tool mocks")}
					</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="definitions" pt="md">
					<SectionCard
						actions={
							<Button
								data-testid="training-definition-new"
								leftSection={<IconPlus size={16} />}
								onClick={() => setEditing(null)}
								size="compact-sm"
							>
								{t("training.definitions.new", "New definition")}
							</Button>
						}
						gap="sm"
						title={t("training.definitions.title", "Dataset definitions")}
					>
						{definitionsQuery.isLoading ? <Loader size="sm" /> : null}
						{!definitionsQuery.isLoading && (definitionsQuery.data ?? []).length === 0 ? (
							<EmptyState
								size="sm"
								message={t("training.definitions.empty", "No dataset definitions yet.")}
								data-testid="training-definitions-empty"
							/>
						) : null}
						<Stack gap="xs">
							{(definitionsQuery.data ?? []).map((definition) => (
								<Group key={definition.id} justify="space-between" data-testid="training-definition-row">
									<Stack gap={2}>
										<Text size="sm" fw={600}>
											{definition.name}
										</Text>
										<Text size="xs" c="dimmed">
											{definition.teacherModelName} · {definition.teacherOutputMode} ·{" "}
											{t("training.definitions.holdout", "{{percent}}% hold-out", {
												percent: Math.round(definition.holdoutFraction * 100),
											})}
										</Text>
									</Stack>
									<Group gap="xs">
										<Badge size="sm" variant="light">
											v{definition.definitionVersion}
										</Badge>
										<Button
											size="compact-xs"
											leftSection={<IconPlayerPlay size={14} />}
											loading={generate.isPending}
											onClick={() => {
												generate.mutate(
													{ definitionId: definition.id, expectedVersion: definition.version, name: definition.name },
													{ onError: requestFailed, onSuccess: () => setTab("datasets") },
												);
											}}
										>
											{t("training.definitions.generate", "Generate")}
										</Button>
										<Button
											data-testid="training-definition-edit"
											leftSection={<IconPencil size={14} />}
											onClick={() => setEditing(definition)}
											size="compact-xs"
											variant="subtle"
										>
											{t("training.definitions.edit", "Edit")}
										</Button>
										<Button
											color="red"
											leftSection={<IconTrash size={14} />}
											loading={deleteDefinition.isPending}
											onClick={() =>
												deleteDefinition.mutate(
													{ definitionId: definition.id, expectedVersion: definition.version },
													{ onError: requestFailed },
												)
											}
											size="compact-xs"
											variant="subtle"
										>
											{t("training.definitions.delete", "Delete")}
										</Button>
									</Group>
								</Group>
							))}
						</Stack>
					</SectionCard>
				</Tabs.Panel>

				<Tabs.Panel value="datasets" pt="md">
					<Stack gap="md">
						<SectionCard gap="sm" title={t("training.datasets.title", "Datasets")}>
							{datasetsQuery.isLoading ? <Loader size="sm" /> : null}
							{!datasetsQuery.isLoading && datasets.length === 0 ? (
								<EmptyState
									size="sm"
									message={t("training.datasets.empty", "No datasets yet. Generate one from a definition.")}
									data-testid="training-datasets-empty"
								/>
							) : null}
							<Stack gap="xs">
								{datasets.map((dataset) => (
									<DatasetRow
										key={dataset.id}
										dataset={dataset}
										isSelected={dataset.id === selectedDatasetId}
										onSelect={() => setSelectedDatasetId(dataset.id === selectedDatasetId ? null : dataset.id)}
										onCancel={() => cancelDataset.mutate({ datasetId: dataset.id }, { onError: requestFailed })}
										onDelete={() =>
											deleteDataset.mutate({ datasetId: dataset.id, expectedVersion: dataset.version }, { onError: requestFailed })
										}
										onExport={(format) =>
											exportDataset.mutate(
												{ datasetId: dataset.id, format },
												{ onError: requestFailed, onSuccess: (result) => setExported({ format, content: result.content }) },
											)
										}
									/>
								))}
							</Stack>
						</SectionCard>

						{selected ? (
							<SectionCard gap="sm" title={t("training.samples.title", "Samples — {{name}}", { name: selected.name })}>
								<DatasetSampleReview dataset={selected} />
							</SectionCard>
						) : null}
					</Stack>
				</Tabs.Panel>

				<Tabs.Panel value="mocks" pt="md">
					<SectionCard gap="sm" title={t("training.mocks.title", "Tool mocks")}>
						{mocksQuery.isLoading ? <Loader size="sm" /> : null}
						{!mocksQuery.isLoading && (mocksQuery.data ?? []).length === 0 ? (
							<EmptyState
								size="sm"
								message={t(
									"training.mocks.empty",
									"No tool mocks yet. A generated call to anything but an approval-free read-only tool needs one.",
								)}
								data-testid="training-mocks-empty"
							/>
						) : null}
						<Stack gap="xs">
							{(mocksQuery.data ?? []).map((mock) => (
								<Group key={mock.id} justify="space-between" data-testid="training-mock-row">
									<Stack gap={2}>
										<Text size="sm" fw={600}>
											{mock.toolName}
										</Text>
										<Text size="xs" c="dimmed">
											{t("training.mocks.rules", "{{count}} rules", { count: mock.ruleCount })}
											{mock.findings.length > 0 ? ` · ${mock.findings[0]}` : ""}
										</Text>
									</Stack>
									<Group gap="xs">
										<Badge
											size="sm"
											color={mock.verificationState === "Verified" ? "teal" : mock.verificationState === "Rejected" ? "red" : "gray"}
										>
											{mock.verificationState}
										</Badge>
										{mock.enabled ? (
											<Badge size="sm" variant="light">
												{t("training.mocks.enabled", "Enabled")}
											</Badge>
										) : null}
										<Button
											size="compact-xs"
											variant="light"
											leftSection={<IconShieldCheck size={14} />}
											loading={verifyMock.isPending}
											onClick={() =>
												verifyMock.mutate(
													{ mockId: mock.id, expectedVersion: mock.version },
													{ onError: (error) => toast.error(apiErrorMessage(error, t("training.errors.request", "The training request failed."))) },
												)
											}
										>
											{t("training.mocks.verify", "Verify")}
										</Button>
									</Group>
								</Group>
							))}
						</Stack>
					</SectionCard>
				</Tabs.Panel>
			</Tabs>

			<DefinitionEditorDialog definition={editing ?? null} onClose={() => setEditing(undefined)} opened={editing !== undefined} />

			<DialogShell
				opened={exported !== null}
				onClose={() => setExported(null)}
				title={t("training.export.title", "Export ({{format}})", { format: exported?.format ?? "" })}
				size="xl"
			>
				{/* A viewport-relative cap rather than a fixed 400px: the dialog is full-screen below 768px, and a
				    landscape phone is shorter than the preview would otherwise claim. */}
				<ScrollArea mah="40vh">
					<Code block={true} data-testid="training-export-content">
						{exported?.content ?? ""}
					</Code>
				</ScrollArea>
			</DialogShell>
		</PageShell>
	);
}

interface DatasetRowProps {
	dataset: TrainingDataset;
	isSelected: boolean;
	onSelect: () => void;
	onCancel: () => void;
	onDelete: () => void;
	onExport: (format: "Jsonl" | "Hermes") => void;
}

function DatasetRow({ dataset, isSelected, onSelect, onCancel, onDelete, onExport }: DatasetRowProps) {
	const { t } = useTranslation();

	return (
		<Group justify="space-between" data-testid="training-dataset-row">
			<UnstyledButton onClick={onSelect} style={{ flex: 1 }}>
				<Stack gap={2}>
					<Group gap="xs">
						<Text size="sm" fw={isSelected ? 700 : 600}>
							{dataset.name}
						</Text>
						<Badge size="sm" color={dataset.status === "Ready" ? "teal" : dataset.status === "Failed" ? "red" : "blue"}>
							{dataset.status}
						</Badge>
						<Badge size="sm" variant="outline">
							{t("training.datasets.revision", "Revision {{revision}}", { revision: dataset.revision })}
						</Badge>
					</Group>
					<Text size="xs" c="dimmed">
						{t("training.datasets.counts", "{{total}} samples · {{good}} good · {{bad}} bad · {{rejected}} rejected · {{duplicate}} duplicate", {
							total: dataset.totalSampleCount,
							good: dataset.goodSampleCount,
							bad: dataset.badSampleCount,
							rejected: dataset.rejectedSampleCount,
							duplicate: dataset.duplicateSampleCount,
						})}
					</Text>
					{dataset.workErrorMessage ? (
						<Text size="xs" c="red">
							{dataset.workErrorMessage}
						</Text>
					) : null}
				</Stack>
			</UnstyledButton>
			<Group gap="xs">
				{isDatasetGenerationCancellable(dataset) ? (
					<Button color="red" data-testid="training-dataset-cancel" onClick={onCancel} size="compact-xs" variant="subtle">
						{t("training.datasets.cancel", "Cancel")}
					</Button>
				) : null}
				<Button size="compact-xs" variant="subtle" leftSection={<IconDownload size={14} />} onClick={() => onExport("Jsonl")}>
					JSONL
				</Button>
				<Button size="compact-xs" variant="subtle" onClick={() => onExport("Hermes")}>
					Hermes
				</Button>
				<Button size="compact-xs" variant="subtle" color="red" leftSection={<IconTrash size={14} />} onClick={onDelete}>
					{t("training.datasets.delete", "Delete")}
				</Button>
			</Group>
		</Group>
	);
}
