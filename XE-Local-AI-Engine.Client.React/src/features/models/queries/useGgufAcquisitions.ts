import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";

import {
	cancelGgufImportMutation,
	getGgufImportCapabilityOptions,
	getGgufImportsOptions,
	getGgufImportsQueryKey,
	getGgufDownloadsOptions,
	getGgufDownloadsQueryKey,
	listLocalModelsQueryKey,
	previewGgufImportMutation,
	startGgufImportMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	type GgufAcquisitionStatus,
	toGgufAcquisitionStatus,
} from "@/features/models/models/GgufAcquisitionModels";

const ACQUISITION_STATUS_CHANGED = "ggufDownload.statusChanged";

export interface StartGgufImportVariables {
	readonly sourcePath: string;
	readonly previewToken: string;
	readonly modelBaseName: string;
	readonly quantization: string;
}

export function useGgufImportCapability() {
	return useQuery(withResponseValidation(getGgufImportCapabilityOptions()));
}

export function usePreviewGgufImport() {
	return useMutation({
		mutationFn: async (sourcePath: string) => {
			const options = withResponseValidation(previewGgufImportMutation());
			return await options.mutationFn?.({ body: { sourcePath } }, undefined as never);
		},
	});
}

export function useStartGgufImport() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (variables: StartGgufImportVariables) => {
			const options = withResponseValidation(startGgufImportMutation());
			return await options.mutationFn?.({ body: variables }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: getGgufImportsQueryKey() }),
	});
}

export function useCancelGgufImport() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (operationId: string) => {
			const options = withResponseValidation(cancelGgufImportMutation());
			return await options.mutationFn?.({ path: { operationId } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: getGgufImportsQueryKey() }),
	});
}

function mergeStatuses(
	previous: ReadonlyMap<string, GgufAcquisitionStatus>,
	rawItems: readonly object[],
): ReadonlyMap<string, GgufAcquisitionStatus> {
	const next = new Map(previous);
	for (const raw of rawItems) {
		const status = toGgufAcquisitionStatus(raw);
		if (status) {
			const current = next.get(status.operationId);
			// A hub push can win the race with the one-shot REST hydrate. ISO-8601 timestamps sort chronologically, so a
			// late hydrate never rolls a newer live phase/byte count backward. Missing timestamps remain compatible and
			// accept the incoming value because older download payloads did not carry them.
			if (!current?.updatedAtUtc || !status.updatedAtUtc || status.updatedAtUtc >= current.updatedAtUtc) {
				next.set(status.operationId, status);
			}
		}
	}
	return next;
}

export function useActiveGgufAcquisitions({ enabled = true }: { enabled?: boolean } = {}): ReadonlyMap<
	string,
	GgufAcquisitionStatus
> {
	const queryClient = useQueryClient();
	const completedHandled = useRef<Set<string>>(new Set());
	const [statuses, setStatuses] = useState<ReadonlyMap<string, GgufAcquisitionStatus>>(() => new Map());
	const downloads = useQuery({ ...withResponseValidation(getGgufDownloadsOptions()), enabled, staleTime: 30_000 });
	const imports = useQuery({ ...withResponseValidation(getGgufImportsOptions()), enabled, staleTime: 30_000 });

	useEffect(() => {
		const items = [...(downloads.data?.items ?? []), ...(imports.data?.items ?? [])];
		if (items.length > 0) {
			setStatuses((previous) => mergeStatuses(previous, items));
		}
	}, [downloads.data, imports.data]);

	useEffect(() => {
		if (!enabled) {
			return;
		}
		const hub = acquireHubConnection("model-fit/gguf/downloads/hub");
		const onStatus = (raw: object): void => {
			const status = toGgufAcquisitionStatus(raw);
			if (status) {
				setStatuses((previous) => new Map(previous).set(status.operationId, status));
			}
		};
		hub.connection.on(ACQUISITION_STATUS_CHANGED, onStatus);
		return () => {
			hub.connection.off(ACQUISITION_STATUS_CHANGED, onStatus);
			hub.release();
		};
	}, [enabled]);

	useEffect(() => {
		for (const status of statuses.values()) {
			if (status.phase === "Completed" && !completedHandled.current.has(status.operationId)) {
				completedHandled.current.add(status.operationId);
				queryClient.invalidateQueries({ queryKey: listLocalModelsQueryKey() }).catch(() => undefined);
			}
		}
	}, [queryClient, statuses]);

	return statuses;
}

export function invalidateGgufAcquisitionHydration(queryClient: ReturnType<typeof useQueryClient>): Promise<unknown[]> {
	return Promise.all([
		queryClient.invalidateQueries({ queryKey: getGgufDownloadsQueryKey() }),
		queryClient.invalidateQueries({ queryKey: getGgufImportsQueryKey() }),
	]);
}
