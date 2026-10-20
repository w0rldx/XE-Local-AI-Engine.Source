import { Button, Group, Loader, Pagination, SegmentedControl, Select, Stack, Table, Text, UnstyledButton } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkRunLivePane } from "@/features/benchmarks/components/BenchmarkRunLivePane";
import type { BenchmarkProjectSummary, BenchmarkReviewQueueState } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkReviewPageSize, benchmarkReviewQueueStates } from "@/features/benchmarks/models/BenchmarkModels";
import { useBenchmarkReviews } from "@/features/benchmarks/queries/useBenchmarks";

const allProjects = "all";

const stateColor: Record<BenchmarkReviewQueueState, string> = {
	pending: "gray",
	stale: "orange",
	approved: "green",
	rejected: "red",
};

/**
 * The review inbox across every review project: one page of runs in one queue state (pending first), each row opening
 * the run pane below it, where the review form lives. Pending covers stale decisions too — a review made under an older
 * rubric no longer counts, so the run is waiting again.
 */
export function BenchmarkReviewInbox({ projects }: { projects: readonly BenchmarkProjectSummary[] }) {
	const { t } = useTranslation();
	const [state, setState] = useState<BenchmarkReviewQueueState>("pending");
	const [projectId, setProjectId] = useState<string | null>(null);
	const [page, setPage] = useState(1);
	const [openRunId, setOpenRunId] = useState<string | null>(null);
	const reviews = useBenchmarkReviews(state, projectId, page);
	const items = reviews.data?.items ?? [];
	const totalPages = Math.max(1, Math.ceil((reviews.data?.totalCount ?? 0) / benchmarkReviewPageSize.default));
	const names = new Map(projects.map((project) => [project.id, project.name]));
	// Reviewing the last row of the last page shrinks the total under the current page; step back rather than strand
	// the reviewer on an empty page whose pager has vanished.
	const lastPage = reviews.data === undefined ? null : totalPages;
	useEffect(() => {
		if (lastPage !== null && page > lastPage) {
			setPage(lastPage);
		}
	}, [lastPage, page]);

	return (
		<SectionCard
			title={t("pages.benchmarks.reviewInbox.title", "Review inbox")}
			actions={
				<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={() => reviews.refetch()}>
					{t("common.refresh", "Refresh")}
				</Button>
			}
		>
			<Stack gap="sm" data-testid="benchmark-review-inbox">
				<Group justify="space-between" align="flex-end">
					<SegmentedControl
						size="xs"
						value={state}
						onChange={(value) => {
							setState(benchmarkReviewQueueStates.find((candidate) => candidate === value) ?? "pending");
							setPage(1);
						}}
						data={benchmarkReviewQueueStates.map((value) => ({
							value,
							label: t(`pages.benchmarks.review.states.${value}`, value),
						}))}
						data-testid="benchmark-review-inbox-state"
					/>
					<Select
						size="xs"
						w={240}
						aria-label={t("pages.benchmarks.reviewInbox.project", "Project")}
						allowDeselect={false}
						value={projectId ?? allProjects}
						onChange={(value) => {
							setProjectId(value === null || value === allProjects ? null : value);
							setPage(1);
						}}
						data={[
							{ value: allProjects, label: t("pages.benchmarks.reviewInbox.allProjects", "All review projects") },
							...projects.map((project) => ({ value: project.id, label: project.name })),
						]}
						data-testid="benchmark-review-inbox-project"
					/>
				</Group>
				{reviews.isLoading ? <Loader size="sm" /> : null}
				{reviews.error ? (
					<InlineErrorAlert
						message={apiErrorMessage(reviews.error, t("pages.benchmarks.errors.reviewsLoad", "Could not load the review inbox."))}
					/>
				) : null}
				{reviews.isSuccess && items.length === 0 ? (
					<EmptyState
						size="sm"
						message={t(`pages.benchmarks.reviewInbox.empty.${state}`, "Nothing here.")}
						data-testid="benchmark-review-inbox-empty"
					/>
				) : null}
				{items.length > 0 ? (
					<Table striped={true} highlightOnHover={true} data-testid="benchmark-review-inbox-table">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.benchmarks.rank.model", "Model")}</Table.Th>
								<Table.Th>{t("pages.benchmarks.reviewInbox.project", "Project")}</Table.Th>
								<Table.Th>{t("pages.benchmarks.reviewInbox.item", "Item")}</Table.Th>
								<Table.Th>{t("pages.benchmarks.reviewInbox.completed", "Completed")}</Table.Th>
								<Table.Th>{t("pages.benchmarks.reviewInbox.state", "State")}</Table.Th>
								<Table.Th>{t("pages.benchmarks.reviewInbox.score", "Score")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{items.map((item) => (
								<Table.Tr key={item.runId} data-testid={`benchmark-review-row-${item.runId}`}>
									<Table.Td>
										<UnstyledButton
											aria-expanded={openRunId === item.runId}
											onClick={() => setOpenRunId((current) => (current === item.runId ? null : item.runId))}
											data-testid={`benchmark-review-open-${item.runId}`}
										>
											<Text size="sm" fw={500} c="blue">
												{item.primaryModelName}
											</Text>
										</UnstyledButton>
									</Table.Td>
									<Table.Td>
										<Text size="sm">{names.get(item.projectId) ?? "—"}</Text>
									</Table.Td>
									<Table.Td>
										<Text size="sm">{item.taskItemIndex === null ? "—" : `#${item.taskItemIndex + 1}`}</Text>
									</Table.Td>
									<Table.Td>
										<Text size="sm">{formatTimestamp(item.primaryCompletedAtUtc)}</Text>
									</Table.Td>
									<Table.Td>
										<StatusBadge
											color={stateColor[item.state]}
											label={t(`pages.benchmarks.review.states.${item.state}`, item.state)}
										/>
									</Table.Td>
									<Table.Td>
										<Text size="sm">{item.reviewScore ?? "—"}</Text>
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				) : null}
				{totalPages > 1 ? (
					<Pagination value={page} onChange={setPage} total={totalPages} size="sm" data-testid="benchmark-review-inbox-pages" />
				) : null}
				{openRunId === null ? null : <BenchmarkRunLivePane key={openRunId} runId={openRunId} />}
			</Stack>
		</SectionCard>
	);
}
