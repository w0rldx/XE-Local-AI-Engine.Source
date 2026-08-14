import { Badge, type MantineColor } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { BenchmarkJudgeStatus, BenchmarkPrimaryStatus } from "@/features/benchmarks/models/BenchmarkModels";

const colors: Record<BenchmarkPrimaryStatus | BenchmarkJudgeStatus, MantineColor> = {
	Queued: "yellow",
	Running: "blue",
	CancelRequested: "orange",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "gray",
	Disabled: "gray",
	Pending: "gray",
	Skipped: "gray",
};

export function BenchmarkStatusBadge({ status }: { status: BenchmarkPrimaryStatus | BenchmarkJudgeStatus }) {
	const { t } = useTranslation();
	return (
		<Badge color={colors[status]} variant="light" aria-label={t(`pages.benchmarks.status.${status}`, status)}>
			{t(`pages.benchmarks.status.${status}`, status)}
		</Badge>
	);
}
