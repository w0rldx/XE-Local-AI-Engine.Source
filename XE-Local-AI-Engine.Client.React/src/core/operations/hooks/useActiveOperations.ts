import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useActiveGgufAcquisitions } from "@/core/model-downloads/queries/useGgufAcquisitions";
import {
	useDatasetBuildOperations,
	useImageJobOperations,
	useImageModelDownloadOperations,
	useKnowledgeIndexingOperation,
	useSchedulerRunOperations,
	useTrainingRunOperations,
	useTranscriptionModelDownloadOperations,
} from "@/core/operations/hooks/useJobOperations";
import {
	type ActiveOperation,
	fromGgufAcquisitions,
	fromRuntimeAcquisition,
	isActiveOperation,
	type Operation,
} from "@/core/operations/models/OperationModels";
import { useRuntimeAcquisitionHub } from "@/core/runtime-acquisition/hooks/useRuntimeAcquisitionHub";
import { useRetryRuntimeAcquisition } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";
import { toast } from "@/core/ui/notifications/Toast";

// Merges the operation feeds that already exist. The GGUF and runtime sources keep their own hydrate-then-push hooks
// on the shared refcounted connections; the job sources (useJobOperations) read the lists their pages read and lease
// a hub only for the one `watch` caller, so the header, Home and the coordinator cost one lease per hub. Benchmark runs
// are not a source: their list is per project, so there is no app-wide snapshot to know which runs to join.

/**
 * Every operation the sources report, terminal ones included; the toast coordinator watches these for transitions.
 * `watch` is for that one coordinator: it owns the job sources' hub leases.
 */
export function useOperations({ watch = false }: { readonly watch?: boolean } = {}): readonly Operation[] {
	const { t } = useTranslation();
	// Neither the hydrate GETs nor the hub negotiations may fire before login (401).
	const enabled = useNodeAuthStore((state) => Boolean(state.accessToken));
	const ggufAcquisitions = useActiveGgufAcquisitions({ enabled });
	const runtimeAcquisition = useRuntimeAcquisitionHub(enabled);
	const { mutate: retryRuntime } = useRetryRuntimeAcquisition();
	// The scheduler and transcription runtime switches 404 their hubs; only a definite "on" from the shared settings read
	// leases one.
	const { data: settings } = useQuery({
		...getNodeSettingsOptions(),
		enabled: enabled && (nodeCapabilities.scheduler || nodeCapabilities.transcription),
	});
	const knowledge = useKnowledgeIndexingOperation({ enabled: enabled && nodeCapabilities.knowledgeBase, watch, t });
	const trainingRuns = useTrainingRunOperations({ enabled: enabled && nodeCapabilities.training, watch, t });
	const datasetBuilds = useDatasetBuildOperations({ enabled: enabled && nodeCapabilities.training, watch, t });
	const imageJobs = useImageJobOperations({ enabled: enabled && nodeCapabilities.images, watch, t });
	const schedulerRuns = useSchedulerRunOperations({
		enabled: enabled && nodeCapabilities.scheduler && settings?.schedulerEnabled === true,
		watch,
		t,
	});
	const imageModelDownloads = useImageModelDownloadOperations({ enabled: enabled && nodeCapabilities.images, watch, t });
	const transcriptionModelDownloads = useTranscriptionModelDownloadOperations({
		enabled: enabled && nodeCapabilities.transcription && settings?.transcriptionEnabled === true,
		watch,
		t,
	});

	return useMemo(() => {
		const retry = (failed: Parameters<typeof retryRuntime>[0]) =>
			retryRuntime(failed, {
				// A refused retry (409: models still running) must say why, not do nothing.
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("operations.runtime.retryError", "The runtime download could not be restarted."))),
			});
		const runtime = fromRuntimeAcquisition(runtimeAcquisition, t, retry);
		return [
			...(runtime === undefined ? [] : [runtime]),
			...fromGgufAcquisitions(ggufAcquisitions.values(), t),
			...(knowledge === undefined ? [] : [knowledge]),
			...trainingRuns,
			...datasetBuilds,
			...imageJobs,
			...schedulerRuns,
			...imageModelDownloads,
			...transcriptionModelDownloads,
		];
	}, [
		ggufAcquisitions,
		runtimeAcquisition,
		retryRuntime,
		t,
		knowledge,
		trainingRuns,
		datasetBuilds,
		imageJobs,
		schedulerRuns,
		imageModelDownloads,
		transcriptionModelDownloads,
	]);
}

/** Running and failed operations, for the Activity card and the header indicator. */
export function useActiveOperations(): readonly ActiveOperation[] {
	const operations = useOperations();
	return useMemo(() => operations.filter(isActiveOperation), [operations]);
}
