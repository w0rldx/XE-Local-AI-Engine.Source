import { Badge, Button, Card, Group, Pagination, Select, Stack, Text } from "@mantine/core";
import { IconCheck, IconX } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { MessageParts } from "@/features/chat/components/MessageParts";
import type { SampleReviewState, TrainingDataset, TrainingSample } from "@/features/training/models/TrainingModels";
import { useReviewTrainingSample, useTrainingSamples } from "@/features/training/queries/useTrainingDatasets";

interface DatasetSampleReviewProps {
	dataset: TrainingDataset;
}

const reviewStateFilters: { value: SampleReviewState | "all"; labelKey: string; fallback: string }[] = [
	{ value: "all", labelKey: "training.samples.filter.all", fallback: "All" },
	{ value: "Pending", labelKey: "training.samples.filter.pending", fallback: "Pending" },
	{ value: "Approved", labelKey: "training.samples.filter.approved", fallback: "Approved" },
	{ value: "Rejected", labelKey: "training.samples.filter.rejected", fallback: "Rejected" },
];

const pageSize = 20;

/** Reviews one dataset's samples. Trajectories render through the shared chat `MessageParts` component. */
export function DatasetSampleReview({ dataset }: DatasetSampleReviewProps) {
	const { t } = useTranslation();
	const [page, setPage] = useState(1);
	const [filter, setFilter] = useState<SampleReviewState | "all">("all");
	const samplesQuery = useTrainingSamples(dataset.id, page, filter === "all" ? undefined : filter);
	const review = useReviewTrainingSample();

	const items = samplesQuery.data?.items ?? [];
	const totalPages = Math.max(1, Math.ceil((samplesQuery.data?.totalCount ?? 0) / pageSize));

	return (
		<Stack gap="sm" data-testid="dataset-sample-review">
			<Group justify="space-between" align="flex-end">
				<Group gap="xs">
					<Badge variant="light" data-testid="dataset-revision">
						{t("training.datasets.revision", "Revision {{revision}}", { revision: dataset.revision })}
					</Badge>
					{dataset.contentFingerprint ? (
						<Text size="xs" c="dimmed" data-testid="dataset-fingerprint">
							{dataset.contentFingerprint.slice(0, 15)}…
						</Text>
					) : null}
				</Group>
				<Select
					size="xs"
					w={160}
					aria-label={t("training.samples.filter.label", "Review state")}
					data={reviewStateFilters.map((option) => ({ value: option.value, label: t(option.labelKey, option.fallback) }))}
					value={filter}
					onChange={(value) => {
						setFilter((value ?? "all") as SampleReviewState | "all");
						setPage(1);
					}}
				/>
			</Group>

			{items.length === 0 && !samplesQuery.isLoading ? (
				<EmptyState size="sm" message={t("training.samples.empty", "No samples match this filter.")} data-testid="training-samples-empty" />
			) : null}

			{items.map((sample) => (
				<SampleCard
					key={sample.id}
					sample={sample}
					disabled={review.isPending}
					onReview={(verb, label) => review.mutate({ datasetId: dataset.id, sampleId: sample.id, verb, label })}
				/>
			))}

			{totalPages > 1 ? <Pagination value={page} onChange={setPage} total={totalPages} size="sm" /> : null}
		</Stack>
	);
}

interface SampleCardProps {
	sample: TrainingSample;
	disabled: boolean;
	onReview: (verb: "Approve" | "Reject" | "Relabel", label?: "Good" | "Bad") => void;
}

function SampleCard({ sample, disabled, onReview }: SampleCardProps) {
	const { t } = useTranslation();
	const failedLayers = sample.validationLayers.filter((layer) => !layer.passed);

	return (
		<Card withBorder={true} padding="sm" data-testid="training-sample-card">
			<Stack gap="xs">
				<Group justify="space-between">
					<Group gap="xs">
						<Text size="sm" fw={600}>
							#{sample.sequence + 1}
						</Text>
						<Badge size="sm" variant="light">
							{sample.kind}
						</Badge>
						<Badge size="sm" color={sample.label === "Good" ? "teal" : "orange"}>
							{sample.label}
						</Badge>
						<Badge size="sm" variant="outline">
							{sample.reviewState}
						</Badge>
					</Group>
					<Group gap="xs">
						<Button
							size="compact-xs"
							variant="light"
							leftSection={<IconCheck size={14} />}
							disabled={disabled}
							onClick={() => onReview("Approve")}
						>
							{t("training.samples.approve", "Approve")}
						</Button>
						<Button
							size="compact-xs"
							variant="light"
							color="red"
							leftSection={<IconX size={14} />}
							disabled={disabled}
							onClick={() => onReview("Reject")}
						>
							{t("training.samples.reject", "Reject")}
						</Button>
						<Button
							size="compact-xs"
							variant="subtle"
							disabled={disabled}
							onClick={() => onReview("Relabel", sample.label === "Good" ? "Bad" : "Good")}
						>
							{t("training.samples.relabel", "Mark as {{label}}", { label: sample.label === "Good" ? "Bad" : "Good" })}
						</Button>
					</Group>
				</Group>

				<MessageParts parts={sample.parts} />

				{failedLayers.length > 0 ? (
					<Stack gap={2} data-testid="training-sample-failures">
						{failedLayers.map((layer) => (
							<Text key={layer.layer} size="xs" c="orange">
								{layer.layer} ({layer.scoredBy}): {layer.reason ?? t("training.samples.layerFailed", "failed")}
							</Text>
						))}
					</Stack>
				) : null}
			</Stack>
		</Card>
	);
}
