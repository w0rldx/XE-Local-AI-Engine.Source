import { useEffect, useState } from "react";

import { useRunStreamSubscription } from "@/core/api/signalr/useRunStreamSubscription";
import type { TrainingRunLiveProgress } from "@/features/training/models/TrainingModels";
import { applyRunEvent, emptyTrainingRunProgress } from "@/features/training/models/TrainingModels";

/**
 * Subscribes to one run's bounded progress stream. A run publishes coarse progress rather than token deltas, so this
 * hook keeps only the latest counters: a gap or a server-pushed replay reset falls back to the authoritative HTTP
 * snapshot (`onResync`) instead of guessing.
 */
export function useTrainingRunHub(runId: string | null, onResync: () => void): TrainingRunLiveProgress {
	const [snapshot, setSnapshot] = useState({ runId, progress: emptyTrainingRunProgress });

	useEffect(() => {
		setSnapshot({ runId, progress: emptyTrainingRunProgress });
	}, [runId]);

	useRunStreamSubscription(runId, {
		onResync,
		onEvent: (event, gap) => {
			if (gap) {
				onResync();
				return;
			}
			setSnapshot((current) => ({ runId, progress: applyRunEvent(current.progress, event) }));
			// A status change moves a row only the HTTP snapshot is authoritative for — the run's own for "State", and an
			// evaluation's for "EvaluationState", which rides this stream because evaluations share their run's group.
			if (event.kind === "State" || event.kind === "EvaluationState") {
				onResync();
			}
		},
	});

	// A new subscription must never render the previous run's counters before its effect resets them.
	return snapshot.runId === runId ? snapshot.progress : emptyTrainingRunProgress;
}
