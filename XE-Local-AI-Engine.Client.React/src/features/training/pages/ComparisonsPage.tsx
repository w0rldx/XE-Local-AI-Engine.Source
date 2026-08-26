import { Button, Stack } from "@mantine/core";
import { IconGitCompare, IconPlus } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { ComparisonCreateDialog } from "@/features/training/components/ComparisonCreateDialog";
import { ComparisonReportCard } from "@/features/training/components/ComparisonReportCard";
import { useComparisonReports, useDeleteComparison } from "@/features/training/queries/useTrainingComparisons";

/**
 * Comparison reports: base versus tuned on the same frozen hold-out samples, per sample kind. Live side-by-side output
 * already exists on the benchmarks page, so this page links there rather than growing a second compare surface.
 */
export function ComparisonsPage() {
	const { t } = useTranslation();
	const [creating, setCreating] = useState(false);
	const reportsQuery = useComparisonReports();
	const deleteMutation = useDeleteComparison();
	const reports = reportsQuery.data ?? [];

	return (
		<PageShell>
			<PageHeader
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={() => setCreating(true)}>
						{t("training.comparisons.list.create", "New comparison")}
					</Button>
				}
				icon={<IconGitCompare size={24} />}
				subtitle={t(
					"pages.training.comparisons.subtitle",
					"Score a base model and its tuned counterpart on the same frozen hold-out samples, then read the difference.",
				)}
				title={t("pages.training.comparisons.title", "Comparisons")}
			/>

			<Stack gap="lg">
				{reports.length === 0 ? (
					<SectionCard title={t("training.comparisons.list.title", "Comparison reports")}>
						<EmptyState
							icon={<IconGitCompare size={28} opacity={0.5} />}
							message={t(
								"training.comparisons.list.empty",
								"No comparison reports yet. Create one from a finished training run.",
							)}
							size="sm"
						/>
					</SectionCard>
				) : (
					reports.map((report) => (
						<ComparisonReportCard
							key={report.id}
							onDelete={() =>
								deleteMutation.mutate(
									{ path: { comparisonId: report.id }, body: { expectedVersion: report.version } },
									{
										onError: (error) =>
											toast.error(
												apiErrorMessage(
													error,
													t("training.comparisons.list.deleteFailed", "Could not delete the comparison report."),
												),
											),
									},
								)
							}
							report={report}
						/>
					))
				)}
			</Stack>

			<ComparisonCreateDialog onClose={() => setCreating(false)} opened={creating} />
		</PageShell>
	);
}
