import { Alert } from "@mantine/core";
import { IconLayoutGrid } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { BenchmarkBatchProgress } from "@/features/benchmarks/models/BenchmarkModels";

interface BenchmarkBatchProgressAlertProps {
	progress: BenchmarkBatchProgress;
	onDismiss: () => void;
}

/**
 * One line of "where is my batch". A matrix launch scatters its runs across a ranked table sorted by score, so the
 * launch itself is otherwise unfollowable — the operator would have to find its rows to learn whether it is done.
 * Derived from the runs already in hand; the launch adds no request of its own.
 */
export function BenchmarkBatchProgressAlert({ progress, onDismiss }: BenchmarkBatchProgressAlertProps) {
	const { t } = useTranslation();
	return (
		<Alert
			color="blue"
			py="xs"
			icon={<IconLayoutGrid size={16} />}
			withCloseButton={true}
			closeButtonLabel={t("common.close", "Close")}
			onClose={onDismiss}
			data-testid="benchmark-batch-progress"
		>
			{t("pages.benchmarks.matrix.progress", "Batch: {{done}} of {{total}} done ({{running}} running, {{queued}} queued, {{failed}} failed)", {
				done: progress.done,
				total: progress.total,
				running: progress.running,
				queued: progress.queued,
				failed: progress.failed,
			})}
		</Alert>
	);
}
