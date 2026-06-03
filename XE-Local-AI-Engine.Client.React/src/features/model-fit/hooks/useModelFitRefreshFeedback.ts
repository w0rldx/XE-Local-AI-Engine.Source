import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	notifyModelFitRefreshRun,
	isTerminalRefreshRunStatus,
} from "@/features/model-fit/notifications/ModelFitRefreshNotifications";
import { useRefreshRecommendations } from "@/features/model-fit/queries/useModelFit";
import type { ScheduledJobRun, ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";
import { useScheduledJobRuns } from "@/features/scheduler/queries/useScheduler";

const pollIntervalMs = 1000;
const watchTimeoutMs = 120000;
const clockSkewToleranceMs = 1000;

interface RefreshWatch {
	readonly scheduledJobId: string;
	readonly requestedAtUtc: number;
	readonly expiresAtUtc: number;
}

export interface ModelFitRefreshFeedback {
	readonly refresh: (scheduledJobId: string) => void;
	readonly isPending: boolean;
	readonly error: unknown;
}

function latestTerminalRun(runs: readonly ScheduledJobRun[], requestedAtUtc: number): ScheduledJobRun | undefined {
	const lowerBound = requestedAtUtc - clockSkewToleranceMs;
	return runs.find((run) => {
		if (run.actualFireTimeUtc === null) {
			return false;
		}
		return run.actualFireTimeUtc >= lowerBound && isTerminalRefreshRunStatus(run.status);
	});
}

function runFilters(watch: RefreshWatch | null): ScheduledJobRunFilters {
	if (watch === null) {
		return {};
	}

	return {
		scheduledJobId: watch.scheduledJobId,
		fromUtc: Math.max(0, watch.requestedAtUtc - clockSkewToleranceMs),
	};
}

export function useModelFitRefreshFeedback(): ModelFitRefreshFeedback {
	const { t } = useTranslation();
	const [watch, setWatch] = useState<RefreshWatch | null>(null);
	const { mutate, isPending, error } = useRefreshRecommendations();
	const filters = useMemo(() => runFilters(watch), [watch]);
	const runsQuery = useScheduledJobRuns(filters, {
		enabled: watch !== null,
		refetchInterval: watch === null ? false : pollIntervalMs,
	});

	useEffect(() => {
		if (watch === null) {
			return;
		}

		if (Date.now() > watch.expiresAtUtc) {
			setWatch(null);
			return;
		}

		const terminalRun = latestTerminalRun(runsQuery.data ?? [], watch.requestedAtUtc);
		if (terminalRun === undefined) {
			return;
		}

		notifyModelFitRefreshRun(terminalRun, t);
		setWatch(null);
	}, [runsQuery.data, t, watch]);

	const refresh = useCallback(
		(scheduledJobId: string): void => {
			const requestedAtUtc = Date.now();
			setWatch({
				scheduledJobId,
				requestedAtUtc,
				expiresAtUtc: requestedAtUtc + watchTimeoutMs,
			});
			mutate(scheduledJobId, {
				onError: () => {
					setWatch(null);
				},
			});
		},
		[mutate],
	);

	return {
		refresh,
		isPending,
		error,
	};
}
