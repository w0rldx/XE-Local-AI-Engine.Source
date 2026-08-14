import type { MantineColor } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
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
	const label = t(`pages.benchmarks.status.${status}`, status);
	return <StatusBadge color={colors[status]} label={label} aria-label={label} />;
}
