import { create } from "zustand";
import { persist } from "zustand/middleware";

import type { TranscriptionSourceKind } from "@/features/transcription/models/TranscriptionModels";

// UI-only preferences for live capture: what the operator picked last time and whether the provisional line is shown.
//
// No transcript data, ever — the transcript is server state and lives in TanStack Query (push-fed for a live session,
// REST-fed once it is terminal). What is kept here is the three answers a reload must not throw away: the source kind
// the operator picks most, which microphone each session was configured for, and whether provisional text is wanted
// on screen. Persisted because a reload mid-session would otherwise lose the device that session belongs to.
//
// Shape is the nested `actions` object (wiki 16's shape for a new store), matching `PendingComposerTextStore` in the
// same feature; the only thing borrowed from `CpuFallbackBannerStore` is the `persist` wrapper.
interface TranscriptionCaptureStoreState {
	readonly lastSourceKind: TranscriptionSourceKind;
	/**
	 * The microphone each session was created with, by session id. Per session rather than one global slot: two
	 * sessions created on different microphones must each capture from their own, and a single remembered id would
	 * also reach `getUserMedia` for hardware that has since been unplugged — which surfaces as "no microphone found"
	 * on a session the operator configured as the system default. A missing entry, and null, both mean "whatever the
	 * browser calls the default input". Never pruned: a session id and a device id are a few dozen bytes each.
	 */
	readonly deviceIdBySession: Readonly<Record<string, string | null>>;
	/**
	 * The application each Windows process-capture session was created against, by session id. A separate map from
	 * `deviceIdBySession` rather than a sentinel in it: a WASAPI process id is a number from a different domain than a
	 * browser `MediaDeviceInfo.deviceId`, and one keyspace would let a microphone id and a process id collide across
	 * two sessions. A missing entry means the pid was never recorded, which the session view reports rather than
	 * guessing a process to capture.
	 */
	readonly processIdBySession: Readonly<Record<string, number | null>>;
	readonly showPartials: boolean;
	readonly actions: {
		readonly setLastSourceKind: (sourceKind: TranscriptionSourceKind) => void;
		readonly rememberDevice: (sessionId: string, deviceId: string | null) => void;
		readonly rememberProcess: (sessionId: string, processId: number | null) => void;
		readonly setShowPartials: (showPartials: boolean) => void;
	};
}

export const useTranscriptionCaptureStore = create<TranscriptionCaptureStoreState>()(
	persist(
		(set) => ({
			lastSourceKind: "File",
			deviceIdBySession: {},
			processIdBySession: {},
			showPartials: true,
			actions: {
				setLastSourceKind: (lastSourceKind) => set({ lastSourceKind }),
				rememberDevice: (sessionId, deviceId) =>
					set((state) => ({ deviceIdBySession: { ...state.deviceIdBySession, [sessionId]: deviceId } })),
				rememberProcess: (sessionId, processId) =>
					set((state) => ({ processIdBySession: { ...state.processIdBySession, [sessionId]: processId } })),
				setShowPartials: (showPartials) => set({ showPartials }),
			},
		}),
		{
			name: "xe-transcription-capture",
			// Actions are behaviour, not state: persisting them would rehydrate a stale closure over an old `set`.
			partialize: (state) => ({
				lastSourceKind: state.lastSourceKind,
				deviceIdBySession: state.deviceIdBySession,
				processIdBySession: state.processIdBySession,
				showPartials: state.showPartials,
			}),
		},
	),
);
