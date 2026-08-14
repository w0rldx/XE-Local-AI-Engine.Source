import { HubConnectionState } from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	applyBenchmarkEvent,
	type BenchmarkOutputPart,
	type BenchmarkRunDetail,
	benchmarkReplayResetSchema,
	benchmarkRunEventSchema,
} from "@/features/benchmarks/models/BenchmarkModels";

export const benchmarkHubEvents = {
	event: "benchmarkRun.event",
	replayReset: "benchmarkRun.replayReset",
} as const;

export interface BenchmarkRunLiveView {
	parts: BenchmarkOutputPart[];
	lastSequence: number;
	isConnected: boolean;
	isReconnecting: boolean;
}

interface UseBenchmarkRunHubOptions {
	run: BenchmarkRunDetail | undefined;
	refetch: () => Promise<BenchmarkRunDetail | undefined>;
}

/**
 * Reconciles one benchmark run's bounded live SignalR stream with its durable HTTP snapshot. The cursor advances only
 * for contiguous events; duplicates are dropped, gaps/reset messages trigger an authoritative refetch, reconnects
 * subscribe from the last applied sequence, and terminal notifications always fall back to encrypted HTTP output.
 */
export function useBenchmarkRunHub({ run, refetch }: UseBenchmarkRunHubOptions): BenchmarkRunLiveView {
	const [parts, setParts] = useState<BenchmarkOutputPart[]>(run?.outputParts ?? []);
	const [lastSequence, setLastSequence] = useState(run?.lastStreamSequence ?? 0);
	const [isConnected, setConnected] = useState(false);
	const [isReconnecting, setReconnecting] = useState(false);
	const cursorRef = useRef(run?.lastStreamSequence ?? 0);
	const refetchRef = useRef(refetch);
	const runRef = useRef(run);
	const activeRunIdRef = useRef(run?.id);
	refetchRef.current = refetch;
	runRef.current = run;
	const serverRevision = run ? `${run.id}:${run.updatedAtUtc}` : "";

	useEffect(() => {
		if (!serverRevision) {
			setParts([]);
			cursorRef.current = 0;
			setLastSequence(0);
			activeRunIdRef.current = undefined;
			return;
		}
		const current = runRef.current;
		if (!current) {
			return;
		}
		const changedRun = activeRunIdRef.current !== current.id;
		if (!changedRun && current.lastStreamSequence < cursorRef.current) {
			return;
		}
		activeRunIdRef.current = current.id;
		setParts(current?.outputParts ?? []);
		const cursor = current?.lastStreamSequence ?? 0;
		cursorRef.current = cursor;
		setLastSequence(cursor);
	}, [serverRevision]);

	useEffect(() => {
		if (!run?.id) {
			return;
		}

		const runId = run.id;
		const hub = acquireHubConnection("benchmarks/hub");
		const { connection } = hub;
		let disposed = false;
		let reconciling = false;

		const reconcileFromHttp = async (authoritative = false): Promise<void> => {
			if (reconciling || disposed) {
				return;
			}
			reconciling = true;
			setReconnecting(true);
			try {
				const current = await refetchRef.current();
				if (!disposed && current?.id === runId && (authoritative || current.lastStreamSequence >= cursorRef.current)) {
					setParts(current.outputParts);
					cursorRef.current = current.lastStreamSequence;
					setLastSequence(current.lastStreamSequence);
				}
			} finally {
				reconciling = false;
				if (!disposed) {
					setReconnecting(false);
				}
			}
		};

		const eventHandler = (value: unknown): void => {
			const parsed = benchmarkRunEventSchema.safeParse(value);
			if (!parsed.success || parsed.data.runId !== runId) {
				return;
			}
			const event = parsed.data;
			if (event.sequence <= cursorRef.current) {
				return;
			}
			if (event.sequence !== cursorRef.current + 1) {
				reconcileFromHttp().catch(() => undefined);
				return;
			}
			cursorRef.current = event.sequence;
			setLastSequence(event.sequence);
			setParts((current) => applyBenchmarkEvent(current, event));
			if (event.kind === "TerminalSnapshotAvailable") {
				reconcileFromHttp(true).catch(() => undefined);
			}
		};
		const resetHandler = (value: unknown): void => {
			const parsed = benchmarkReplayResetSchema.safeParse(value);
			if (parsed.success && parsed.data.runId === runId) {
				reconcileFromHttp(true).catch(() => undefined);
			}
		};
		connection.on(benchmarkHubEvents.event, eventHandler);
		connection.on(benchmarkHubEvents.replayReset, resetHandler);

		const subscribe = (): void => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			setConnected(true);
			setReconnecting(false);
			connection.invoke("Subscribe", runId, cursorRef.current).catch(() => {
				if (!disposed) {
					setConnected(false);
					setReconnecting(true);
					reconcileFromHttp().catch(() => undefined);
				}
			});
		};
		hub.whenStarted.then(subscribe);
		hub.onReconnected(() => {
			setReconnecting(true);
			subscribe();
		});

		return () => {
			disposed = true;
			connection.off(benchmarkHubEvents.event, eventHandler);
			connection.off(benchmarkHubEvents.replayReset, resetHandler);
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("Unsubscribe", runId).catch(() => undefined);
			}
			hub.release();
		};
	}, [run?.id]);

	return { parts, lastSequence, isConnected, isReconnecting };
}
