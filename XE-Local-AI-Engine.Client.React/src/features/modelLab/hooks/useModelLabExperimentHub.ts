import { useEffect, useState } from "react";

import { useRunStreamSubscription } from "@/core/api/signalr/useRunStreamSubscription";

export interface ModelLabLiveProgress {
	phase: string | null;
	step: number | null;
	totalSteps: number | null;
	vramBytes: number | null;
}

export interface ModelLabExperimentHubState extends ModelLabLiveProgress {
	/** True once the hub has accepted this subscription; false while it is starting, reconnecting or closed, so callers poll. */
	isLive: boolean;
}

const emptyProgress: ModelLabLiveProgress = { phase: null, step: null, totalSteps: null, vramBytes: null };

/**
 * One experiment's progress, over the run hub an experiment shares with training runs. The saved row is the truth: a gap,
 * a reset or a state change falls back to it through `onResync`.
 */
export function useModelLabExperimentHub(experimentId: string | null, onResync: () => void): ModelLabExperimentHubState {
	const [snapshot, setSnapshot] = useState({ experimentId, progress: emptyProgress });
	const [isLive, setIsLive] = useState(false);

	useEffect(() => {
		setSnapshot({ experimentId, progress: emptyProgress });
	}, [experimentId]);

	useRunStreamSubscription(experimentId, {
		onResync,
		onLiveChange: setIsLive,
		onEvent: (event, gap) => {
			const { phase, step, totalSteps, vramBytes } = event.payload;
			setSnapshot((current) => ({
				experimentId,
				progress: {
					phase: phase ?? current.progress.phase,
					// A count belongs to its phase: the next one must not start on the last one's total.
					step: step ?? (phase ? null : current.progress.step),
					totalSteps: totalSteps ?? (phase ? null : current.progress.totalSteps),
					vramBytes: vramBytes ?? current.progress.vramBytes,
				},
			}));
			// A phase can follow a saved arm, whose outputs only the row holds; the hub carries no event of its own for that.
			if (gap || event.kind === "State" || event.kind === "Phase") {
				onResync();
			}
		},
	});

	return { ...(snapshot.experimentId === experimentId ? snapshot.progress : emptyProgress), isLive };
}
