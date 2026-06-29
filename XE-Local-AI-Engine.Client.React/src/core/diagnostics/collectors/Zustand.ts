// Zustand middleware factory for opted-in stores (plan §7.2): record action + redacted shallow diff.
//
// Usage:  create<T>()(withDiagnostics("storeName", (set, get, api) => ({ ... })))
//
// A tiny inline shallow diff is used (no `microdiff` dependency added in this lane). Only top-level
// keys whose reference changed are reported; values are redacted by the buffer on push.

import type { StateCreator, StoreMutatorIdentifier } from "zustand";

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import type { StateDiffField } from "@/core/diagnostics/Types";

type WithDiagnostics = <
	T,
	Mps extends [StoreMutatorIdentifier, unknown][] = [],
	Mcs extends [StoreMutatorIdentifier, unknown][] = [],
>(
	storeName: string,
	config: StateCreator<T, Mps, Mcs>,
) => StateCreator<T, Mps, Mcs>;

type SetState<T> = (partial: T | Partial<T> | ((state: T) => T | Partial<T>), replace?: boolean | undefined) => void;

const withDiagnosticsImpl =
	<T>(storeName: string, config: StateCreator<T, [], []>): StateCreator<T, [], []> =>
	(set, get, api) => {
		const tracedSet: SetState<T> = (partial, replace) => {
			const previous = get();
			(set as SetState<T>)(partial, replace);
			const next = get();
			const diff = shallowDiff(previous, next);
			if (diff.length > 0) {
				push({ category: "state", store: storeName, diff });
			}
		};

		return config(tracedSet as typeof set, get, api);
	};

export const withDiagnostics = withDiagnosticsImpl as unknown as WithDiagnostics;

function shallowDiff(previous: unknown, next: unknown): StateDiffField[] {
	if (!isRecord(previous) || !isRecord(next)) {
		return [];
	}
	const fields: StateDiffField[] = [];
	for (const key of new Set([...Object.keys(previous), ...Object.keys(next)])) {
		// Skip the conventional `actions` bag — function identities never change and add noise.
		if (key === "actions") {
			continue;
		}
		if (previous[key] !== next[key]) {
			fields.push({ key, from: previous[key], to: next[key] });
		}
	}
	return fields;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}
