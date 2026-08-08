import { create } from "zustand";

import type { ChatSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";

// Persisted per-send sampling overrides, global like the other composer preferences. Stored as a
// single JSON blob under one key so the shape can evolve without adding new storage keys.
const SAMPLING_OPTIONS_STORAGE_KEY = "xe-node-chat-sampling-options";

interface ChatSamplingPreferencesStore {
	options: ChatSamplingOptions;
	actions: {
		setField: <K extends keyof ChatSamplingOptions>(key: K, value: ChatSamplingOptions[K]) => void;
		reset: () => void;
	};
}

// Numeric fields that must be finite JS numbers in the persisted shape. Any value that is not a
// finite number (e.g. a string from a partial Mantine NumberInput that was stored before the
// dialog-layer coercion fix) is silently dropped on hydrate so stale localStorage never reaches
// the wire as a string.
const NUMERIC_SAMPLING_KEYS: ReadonlyArray<keyof Omit<ChatSamplingOptions, "stop">> = [
	"temperature",
	"topP",
	"topK",
	"minP",
	"maxOutputTokens",
	"repeatPenalty",
	"repeatLastN",
	"presencePenalty",
	"frequencyPenalty",
	"seed",
	"numCtx",
];

function readStoredOptions(): ChatSamplingOptions {
	try {
		const raw = globalThis.localStorage?.getItem(SAMPLING_OPTIONS_STORAGE_KEY);
		if (!raw) {
			return {};
		}
		const parsed: unknown = JSON.parse(raw);
		// Validate that parsed is a plain object; bad JSON or wrong shape → fall back to defaults.
		if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
			return {};
		}
		const obj = parsed as Record<string, unknown>;
		const result: ChatSamplingOptions = {};

		// Sanitize numeric fields — only accept finite numbers to guard against string entries
		// from older builds that lacked the dialog-layer coercion.
		for (const key of NUMERIC_SAMPLING_KEYS) {
			const val = obj[key];
			if (val != null) {
				const n = Number(val);
				if (Number.isFinite(n)) {
					(result as Record<string, number>)[key] = n;
				}
			}
		}

		// stop[] is valid only when it is an array of strings.
		if (Array.isArray(obj["stop"]) && obj["stop"].every((s) => typeof s === "string")) {
			result.stop = obj["stop"] as string[];
		}

		return result;
	} catch {
		return {};
	}
}

function writeStoredOptions(options: ChatSamplingOptions): void {
	try {
		globalThis.localStorage?.setItem(SAMPLING_OPTIONS_STORAGE_KEY, JSON.stringify(options));
	} catch {
		// Ignore unavailable storage or quota errors; the in-memory state still updates.
	}
}

export const useChatSamplingPreferencesStore = create<ChatSamplingPreferencesStore>()((set) => ({
	options: readStoredOptions(),
	actions: {
		setField: (key, value) => {
			set((state) => {
				const next = { ...state.options, [key]: value };
				writeStoredOptions(next);
				return { options: next };
			});
		},
		reset: () => {
			const empty: ChatSamplingOptions = {};
			writeStoredOptions(empty);
			set({ options: empty });
		},
	},
}));
