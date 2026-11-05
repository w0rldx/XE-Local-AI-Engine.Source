import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useActiveGgufAcquisitions } from "@/core/model-downloads/queries/useGgufAcquisitions";
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

// Merges the operation feeds that already exist: no polling or hub of its own. Each source hook keeps its own
// hydrate-then-push contract, and its hub connection is the shared refcounted one, so several consumers of this hook
// cost one connection per hub. Image and transcription model downloads are not sources: their hooks are
// feature-scoped page polls without a hub, and an app-wide consumer would turn them into app-wide polling.

/** Every operation the sources report, terminal ones included; the toast coordinator watches these for transitions. */
export function useOperations(): readonly Operation[] {
	const { t } = useTranslation();
	// Neither the hydrate GETs nor the hub negotiations may fire before login (401).
	const enabled = useNodeAuthStore((state) => Boolean(state.accessToken));
	const ggufAcquisitions = useActiveGgufAcquisitions({ enabled });
	const runtimeAcquisition = useRuntimeAcquisitionHub(enabled);
	const { mutate: retryRuntime } = useRetryRuntimeAcquisition();

	return useMemo(() => {
		const retry = (failed: Parameters<typeof retryRuntime>[0]) =>
			retryRuntime(failed, {
				// A refused retry (409: models still running) must say why, not do nothing.
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("operations.runtime.retryError", "The runtime download could not be restarted."))),
			});
		const runtime = fromRuntimeAcquisition(runtimeAcquisition, t, retry);
		const gguf = fromGgufAcquisitions(ggufAcquisitions.values(), t);
		return runtime === undefined ? gguf : [runtime, ...gguf];
	}, [ggufAcquisitions, runtimeAcquisition, retryRuntime, t]);
}

/** Running and failed operations, for the Activity card and the header indicator. */
export function useActiveOperations(): readonly ActiveOperation[] {
	const operations = useOperations();
	return useMemo(() => operations.filter(isActiveOperation), [operations]);
}
