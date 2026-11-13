import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	appendLiveOutputLines,
	type LiveOutputBuffer,
	type LiveOutputLine,
} from "@/core/ui/components/LiveOutputView/LiveOutputLines";
import type {
	DevelopmentAttemptLiveUpdate,
	DevelopmentAttemptSubscriptionSnapshot,
} from "@/features/development/models/DevelopmentModels";
import { developmentInvalidationKey, developmentQueryIds } from "@/features/development/queries/useDevelopment";

const EVENT_NAME = "developmentAttemptUpdate";
const MAX_RETAINED_UPDATES = 200;
const MAX_RETAINED_COMMANDS = 20;

/** One command's live output, folded from its `Command` and `CommandOutput` updates and keyed by execution id. */
export interface DevelopmentCommandOutput {
	readonly executionId: string;
	readonly commandId: string;
	readonly output: LiveOutputBuffer;
	readonly skippedLines: number;
	readonly running: boolean;
}

export interface DevelopmentAttemptLiveState {
	readonly connectionState: "idle" | "connecting" | "connected" | "reconnecting" | "unavailable";
	readonly watermark: number;
	readonly droppedOrCoalescedUpdateCount: number;
	readonly latest: DevelopmentAttemptLiveUpdate | null;
	// Every update but `CommandOutput`, which folds into `commands` so an output flood cannot evict this history.
	readonly updates: readonly DevelopmentAttemptLiveUpdate[];
	readonly commands: readonly DevelopmentCommandOutput[];
}

const emptyState: DevelopmentAttemptLiveState = {
	connectionState: "idle",
	watermark: 0,
	droppedOrCoalescedUpdateCount: 0,
	latest: null,
	updates: [],
	commands: [],
};

/** Splits a newline-joined output batch (trailing newline included) into lines; the batch has no stderr split. */
function outputDeltaLines(delta: string | null | undefined): LiveOutputLine[] {
	const texts = (delta ?? "").split("\n");
	if (texts.at(-1) === "") {
		texts.pop();
	}
	return texts.map((text) => ({ stream: "stdout", text }));
}

/**
 * Folds one update into the per-command output list. A `Command` update is a completion when it carries the skipped
 * line count (the backend's `CommandCompleted` always sets it, `CommandStarted` never does) and a start otherwise;
 * output for an unseen execution (a subscription that joined mid-command) starts it too. A completion whose start was
 * missed is dropped, since there is no output to show for it. `Terminal` ends whatever still runs.
 */
function foldCommandOutput(
	commands: readonly DevelopmentCommandOutput[],
	update: DevelopmentAttemptLiveUpdate,
): readonly DevelopmentCommandOutput[] {
	if (update.kind === "Terminal") {
		return commands.some((command) => command.running)
			? commands.map((command) => (command.running ? { ...command, running: false } : command))
			: commands;
	}

	const executionId = update.currentCommandExecutionId;
	if ((update.kind !== "Command" && update.kind !== "CommandOutput") || !executionId) {
		return commands;
	}

	const existing = commands.find((command) => command.executionId === executionId);
	const isOutput = update.kind === "CommandOutput";
	const isCompletion = !isOutput && update.commandOutputSkippedLines != null;
	if (existing === undefined) {
		if (isCompletion) {
			return commands;
		}
		const started: DevelopmentCommandOutput = {
			executionId,
			commandId: update.currentCommandId ?? "",
			output: appendLiveOutputLines(undefined, isOutput ? outputDeltaLines(update.outputDelta) : [], false),
			skippedLines: update.commandOutputSkippedLines ?? 0,
			running: true,
		};
		return [...commands, started].slice(-MAX_RETAINED_COMMANDS);
	}

	const next: DevelopmentCommandOutput = {
		...existing,
		output: isOutput ? appendLiveOutputLines(existing.output, outputDeltaLines(update.outputDelta), false) : existing.output,
		skippedLines: update.commandOutputSkippedLines ?? existing.skippedLines,
		running: isCompletion ? false : existing.running,
	};
	return commands.map((command) => (command === existing ? next : command));
}

/**
 * Which stream of an attempt id is watched. Validation streams under the coder attempt it judges, as a NEW stream with
 * fresh local state. Sequences are node-wide, so its snapshot watermark already covers every coder-run number, and a
 * late coder frame still drained into the same hub group (its Terminal included) falls at or below it and is dropped.
 */
export type DevelopmentLivePhase = "attempt" | "validation";

/**
 * Streams one attempt's live updates; a change of `phase` starts a fresh subscription with fresh state. `retryToken`
 * re-attempts a subscription the hub refused once it changes (the caller passes the project poll's update time): a
 * validation stream starts a moment after the task reports Validation, so its first subscribe can lose that race.
 */
export function useDevelopmentAttemptHub(
	projectId: string | null,
	taskId: string | null,
	attemptId: string | null,
	phase: DevelopmentLivePhase = "attempt",
	retryToken = 0,
) {
	const queryClient = useQueryClient();
	const [state, setState] = useState<DevelopmentAttemptLiveState>(emptyState);
	const retryRef = useRef<(() => void) | null>(null);

	useEffect(() => {
		if (retryToken !== 0) {
			retryRef.current?.();
		}
	}, [retryToken]);

	// biome-ignore lint/correctness/useExhaustiveDependencies: phase keys the subscription; a new stream gets fresh local state.
	useEffect(() => {
		if (!projectId || !taskId || !attemptId) {
			setState(emptyState);
			return;
		}

		const hub = acquireHubConnection("development/hub");
		const { connection } = hub;
		let disposed = false;
		let refused = false;
		let terminated = false;
		let snapshotResolved = false;
		let watermark = 0;
		let buffered: DevelopmentAttemptLiveUpdate[] = [];
		const seenSequences = new Set<number>();

		setState({ ...emptyState, connectionState: "connecting" });

		const retain = (update: DevelopmentAttemptLiveUpdate): void => {
			if (
				update.projectId !== projectId ||
				update.taskId !== taskId ||
				update.attemptId !== attemptId ||
				seenSequences.has(update.sequence)
			) {
				return;
			}
			seenSequences.add(update.sequence);
			watermark = Math.max(watermark, update.sequence);
			setState((current) => ({
				...current,
				watermark,
				latest: update,
				updates: update.kind === "CommandOutput" ? current.updates : [...current.updates, update].slice(-MAX_RETAINED_UPDATES),
				commands: foldCommandOutput(current.commands, update),
			}));
			if (update.kind === "Terminal") {
				// The stream is over: nothing will publish to it again, so a later poll must not subscribe once more.
				terminated = true;
				queryClient
					.invalidateQueries({ queryKey: developmentInvalidationKey(developmentQueryIds.getProject) })
					.catch(() => undefined);
			}
		};

		const onUpdate = (update: DevelopmentAttemptLiveUpdate): void => {
			if (!snapshotResolved) {
				buffered.push(update);
				return;
			}
			if (update.sequence > watermark) {
				retain(update);
			}
		};

		const subscribe = async (reconnecting: boolean): Promise<void> => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			snapshotResolved = false;
			buffered = [];
			setState((current) => ({ ...current, connectionState: reconnecting ? "reconnecting" : "connecting" }));
			try {
				const snapshot = await connection.invoke<DevelopmentAttemptSubscriptionSnapshot>(
					"SubscribeAsync",
					projectId,
					taskId,
					attemptId,
				);
				if (disposed) {
					return;
				}
				const retainedWatermark = watermark;
				watermark = Math.max(watermark, snapshot.watermark);
				setState((current) => ({
					...current,
					connectionState: "connected",
					watermark,
					droppedOrCoalescedUpdateCount: snapshot.droppedOrCoalescedUpdateCount,
				}));
				if (snapshot.latest && snapshot.latest.sequence > retainedWatermark && !seenSequences.has(snapshot.latest.sequence)) {
					retain(snapshot.latest);
				}
				snapshotResolved = true;
				for (const update of buffered.sort((left, right) => left.sequence - right.sequence)) {
					if (update.sequence > watermark) {
						retain(update);
					}
				}
				buffered = [];
				refused = false;
			} catch {
				if (!disposed) {
					refused = true;
					setState((current) => ({ ...current, connectionState: "unavailable" }));
				}
			}
		};

		retryRef.current = () => {
			if (refused && !terminated) {
				subscribe(false).catch(() => undefined);
			}
		};
		connection.on(EVENT_NAME, onUpdate);
		const removeReconnect = hub.onReconnected(() => {
			subscribe(true).catch(() => undefined);
		});
		hub.whenStarted.then(() => subscribe(false)).catch(() => undefined);

		return () => {
			disposed = true;
			retryRef.current = null;
			connection.off(EVENT_NAME, onUpdate);
			removeReconnect();
			hub.release();
		};
	}, [attemptId, phase, projectId, queryClient, taskId]);

	return state;
}
