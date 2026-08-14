import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";

import {
	cancelGgufImportMutation,
	getGgufImportCapabilityOptions,
	getGgufImportsOptions,
	getGgufImportsQueryKey,
	getGgufDownloadsOptions,
	listLocalModelsQueryKey,
	previewGgufImportMutation,
	startGgufImportMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	type GgufAcquisitionStatus,
	isTerminalAcquisitionPhase,
	toGgufAcquisitionStatus,
} from "@/features/models/models/GgufAcquisitionModels";

const ACQUISITION_STATUS_CHANGED = "ggufDownload.statusChanged";
export const ACQUISITION_TERMINAL_RETENTION_LIMIT = 256;
const ACQUISITION_TERMINAL_RETENTION_MS = 24 * 60 * 60 * 1000;

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

function updatedAtMs(status: GgufAcquisitionStatus): number | undefined {
	if (!status.updatedAtUtc) {
		return undefined;
	}
	const parsed = Date.parse(status.updatedAtUtc);
	return Number.isNaN(parsed) ? undefined : parsed;
}

function shouldAcceptAcquisitionStatus(
	current: GgufAcquisitionStatus | undefined,
	incoming: GgufAcquisitionStatus,
): boolean {
	if (!current) {
		return true;
	}
	const currentUpdatedAt = updatedAtMs(current);
	const incomingUpdatedAt = updatedAtMs(incoming);
	if (currentUpdatedAt !== undefined && incomingUpdatedAt === undefined) {
		return false;
	}
	if (currentUpdatedAt !== undefined && incomingUpdatedAt !== undefined) {
		if (incomingUpdatedAt < currentUpdatedAt) {
			return false;
		}
		if (incomingUpdatedAt > currentUpdatedAt) {
			return true;
		}
	}
	return !(isTerminalAcquisitionPhase(current.phase) && !isTerminalAcquisitionPhase(incoming.phase));
}

export function pruneAcquisitionStatuses(
	statuses: ReadonlyMap<string, GgufAcquisitionStatus>,
	nowMs = Date.now(),
): ReadonlyMap<string, GgufAcquisitionStatus> {
	const cutoff = nowMs - ACQUISITION_TERMINAL_RETENTION_MS;
	const active: [string, GgufAcquisitionStatus][] = [];
	const terminal: [string, GgufAcquisitionStatus][] = [];
	for (const entry of statuses) {
		const status = entry[1];
		if (!isTerminalAcquisitionPhase(status.phase)) {
			active.push(entry);
			continue;
		}
		const timestamp = updatedAtMs(status);
		if (timestamp === undefined || timestamp >= cutoff) {
			terminal.push(entry);
		}
	}
	terminal.sort((left, right) => {
		const timestampDifference = (updatedAtMs(right[1]) ?? Number.MAX_SAFE_INTEGER) - (updatedAtMs(left[1]) ?? Number.MAX_SAFE_INTEGER);
		return timestampDifference || left[0].localeCompare(right[0]);
	});
	return new Map([...active, ...terminal.slice(0, ACQUISITION_TERMINAL_RETENTION_LIMIT)]);
}

export function pruneCompletedHandled(completed: ReadonlyMap<string, number>, nowMs = Date.now()): ReadonlyMap<string, number> {
	const cutoff = nowMs - ACQUISITION_TERMINAL_RETENTION_MS;
	return new Map(
		[...completed]
			.filter((entry) => entry[1] >= cutoff)
			.sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]))
			.slice(0, ACQUISITION_TERMINAL_RETENTION_LIMIT),
	);
}

export function mergeStatuses(
	previous: ReadonlyMap<string, GgufAcquisitionStatus>,
	rawItems: readonly object[],
	nowMs = Date.now(),
): ReadonlyMap<string, GgufAcquisitionStatus> {
	const next = new Map(previous);
	for (const raw of rawItems) {
		const status = toGgufAcquisitionStatus(raw);
		if (status) {
			const current = next.get(status.operationId);
			// Hub pushes and REST hydration share the same monotonic guard. Once a timestamped or terminal status is held,
			// an older/unstamped payload cannot roll the phase or byte count backward. Two unstamped legacy download
			// payloads remain compatible, while terminal state still wins their otherwise ambiguous ordering.
			if (shouldAcceptAcquisitionStatus(current, status)) {
				next.set(status.operationId, status);
			}
		}
	}
	return pruneAcquisitionStatuses(next, nowMs);
}

export function useActiveGgufAcquisitions({ enabled = true }: { enabled?: boolean } = {}): ReadonlyMap<
	string,
	GgufAcquisitionStatus
> {
	const queryClient = useQueryClient();
	const completedHandled = useRef<ReadonlyMap<string, number>>(new Map());
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
				setStatuses((previous) => mergeStatuses(previous, [raw]));
			}
		};
		hub.connection.on(ACQUISITION_STATUS_CHANGED, onStatus);
		return () => {
			hub.connection.off(ACQUISITION_STATUS_CHANGED, onStatus);
			hub.release();
		};
	}, [enabled]);

	useEffect(() => {
		const now = Date.now();
		completedHandled.current = pruneCompletedHandled(completedHandled.current, now);
		for (const status of statuses.values()) {
			if (status.phase === "Completed" && !completedHandled.current.has(status.operationId)) {
				completedHandled.current = pruneCompletedHandled(
					new Map(completedHandled.current).set(status.operationId, updatedAtMs(status) ?? now),
					now,
				);
				queryClient.invalidateQueries({ queryKey: listLocalModelsQueryKey() }).catch(() => undefined);
			}
		}
	}, [queryClient, statuses]);

	return statuses;
}
