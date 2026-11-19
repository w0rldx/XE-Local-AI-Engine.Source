import { useState } from "react";
import type { z } from "zod";

import { type RunStreamChannel, useRunStreamSubscription } from "@/core/api/signalr/useRunStreamSubscription";
import type { DatasetGenerationEvent, DatasetGenerationProgress } from "@/features/training/models/TrainingModels";
import {
	applyGenerationEvent,
	datasetGenerationEventSchema,
	datasetGenerationReplayResetSchema,
} from "@/features/training/models/TrainingModels";

const datasetGenerationStream: RunStreamChannel<DatasetGenerationEvent, z.infer<typeof datasetGenerationReplayResetSchema>> = {
	hubPath: "training/datasets/hub",
	eventName: "datasetGeneration.event",
	replayResetName: "datasetGeneration.replayReset",
	eventSchema: datasetGenerationEventSchema,
	resetSchema: datasetGenerationReplayResetSchema,
	idOf: (frame) => frame.datasetId,
};

const emptyProgress: DatasetGenerationProgress = { completed: 0, total: 0, rejected: 0, state: null };

/**
 * Subscribes to one dataset's bounded generation stream. Generation events are coarse progress rather than token
 * deltas, so this hook keeps only a counter: a gap or a server-pushed replay reset falls back to the authoritative
 * HTTP snapshot (`onResync`) instead of guessing.
 */
export function useDatasetGenerationHub(datasetId: string | null, onResync: () => void): DatasetGenerationProgress {
	const [progress, setProgress] = useState<DatasetGenerationProgress>(emptyProgress);
	// Another dataset, or none, starts from zero.
	const [trackedId, setTrackedId] = useState(datasetId);
	if (trackedId !== datasetId) {
		setTrackedId(datasetId);
		setProgress(emptyProgress);
	}

	useRunStreamSubscription(datasetGenerationStream, datasetId, {
		onResync,
		onEvent: (event, gap) => {
			if (gap) {
				onResync();
				return;
			}
			setProgress((current) => applyGenerationEvent(current, event));
			if (event.kind === "State") {
				onResync();
			}
		},
	});

	return progress;
}
