import { t } from "i18next";
import { describe, expect, it, vi } from "vitest";

import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";
import {
	fromGgufAcquisitions,
	fromRuntimeAcquisition,
	isActiveOperation,
	type Operation,
	operationTransitions,
} from "@/core/operations/models/OperationModels";
import type { RuntimeAcquisitionStatus } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";

function gguf(overrides: Partial<GgufAcquisitionStatus> = {}): GgufAcquisitionStatus {
	return {
		operationId: "op-1",
		operationKind: "Download",
		modelName: "qwen3-8b",
		phase: "Downloading",
		pct: undefined,
		completedBytes: null,
		totalBytes: null,
		startedAtUtc: "2026-11-05T10:00:00Z",
		updatedAtUtc: "2026-11-05T10:01:00Z",
		errorCode: null,
		sanitizedMessage: null,
		...overrides,
	};
}

function runtime(overrides: Partial<RuntimeAcquisitionStatus> & { phase: string }): RuntimeAcquisitionStatus {
	return {
		sequence: 1,
		variant: "Cuda",
		tag: "b9692",
		completedBytes: null,
		totalBytes: null,
		stepIndex: 1,
		stepCount: 1,
		...overrides,
	};
}

const noRetry = () => undefined;

describe("fromGgufAcquisitions", () => {
	it("reports the source percentage as known progress and the byte counts as detail", () => {
		const [row] = fromGgufAcquisitions([gguf({ pct: 25, completedBytes: 1024, totalBytes: 4096 })], t);

		expect(row).toMatchObject({
			id: "gguf-download:op-1",
			kind: "gguf-download",
			label: "Downloading qwen3-8b",
			state: "running",
			progress: { kind: "known", pct: 25 },
			detail: "1 KiB of 4 KiB",
			href: { to: "/models" },
		});
	});

	it("stays indeterminate when the source reported no percentage", () => {
		const [row] = fromGgufAcquisitions([gguf({ phase: "Queued" })], t);

		expect(row?.progress).toEqual({ kind: "indeterminate" });
		expect(row?.detail).toBeUndefined();
	});

	it("labels an import as an import", () => {
		const [row] = fromGgufAcquisitions([gguf({ operationKind: "Import", phase: "Copying" })], t);

		expect(row).toMatchObject({ id: "gguf-import:op-1", kind: "gguf-import", label: "Importing qwen3-8b" });
	});

	it("explains a failure by its translated error code and offers no retry it does not have", () => {
		const [row] = fromGgufAcquisitions(
			[gguf({ phase: "Failed", errorCode: "InsufficientStorage", sanitizedMessage: "Need 5 GB, 1 GB free." })],
			t,
		);

		expect(row).toMatchObject({
			state: "failed",
			label: "qwen3-8b could not be downloaded",
			detail: "There is not enough free storage for this download.",
		});
		expect(row?.retry).toBeUndefined();
	});

	it("falls back to the generic sentence for an unknown code, and keeps the server text only after a DownloadFailed", () => {
		const [unknown] = fromGgufAcquisitions([gguf({ phase: "Failed", errorCode: "Future", sanitizedMessage: "Raw." })], t);
		const [generic] = fromGgufAcquisitions(
			[gguf({ phase: "Failed", errorCode: "DownloadFailed", sanitizedMessage: "Download failed." })],
			t,
		);
		const [imported] = fromGgufAcquisitions(
			[gguf({ operationKind: "Import", phase: "Failed", errorCode: "SourceNotFound", sanitizedMessage: "Raw." })],
			t,
		);

		expect(unknown?.detail).toBe("The model could not be downloaded.");
		expect(generic?.detail).toBe("The model could not be downloaded.\nDownload failed.");
		expect(imported?.detail).toBe("The selected GGUF file was not found.");
	});

	it("drops a cancelled operation and keeps a completed one as succeeded", () => {
		const rows = fromGgufAcquisitions(
			[gguf({ operationId: "a", phase: "Cancelled" }), gguf({ operationId: "b", phase: "Completed", pct: 100 })],
			t,
		);

		expect(rows.map((row) => [row.id, row.state, row.label])).toEqual([
			["gguf-download:b", "succeeded", "qwen3-8b is downloaded"],
		]);
		expect(rows.filter(isActiveOperation)).toEqual([]);
	});
});

describe("fromRuntimeAcquisition", () => {
	it("is no operation before a status arrives or while the host is idle", () => {
		expect(fromRuntimeAcquisition(undefined, t, noRetry)).toBeUndefined();
		expect(fromRuntimeAcquisition(runtime({ phase: "Idle" }), t, noRetry)).toBeUndefined();
	});

	it("is determinate only while bytes move against a known total", () => {
		const downloading = fromRuntimeAcquisition(
			runtime({ phase: "Downloading", completedBytes: 1024, totalBytes: 4096 }),
			t,
			noRetry,
		);
		const noTotal = fromRuntimeAcquisition(runtime({ phase: "Downloading", completedBytes: 1024 }), t, noRetry);

		expect(downloading).toMatchObject({
			id: "runtime-acquisition:llamacpp",
			state: "running",
			label: "Setting up the local AI runtime",
			progress: { kind: "known", pct: 25 },
			detail: "Downloading the llama.cpp runtime… · 1 KiB of 4 KiB",
			href: { to: "/node-settings", search: { section: "runtimes" } },
		});
		expect(noTotal?.progress).toEqual({ kind: "indeterminate" });
		expect(noTotal?.detail).toBe("Downloading the llama.cpp runtime… · 1 KiB downloaded");
	});

	it("names the step only on the multi-archive path", () => {
		const row = fromRuntimeAcquisition(runtime({ phase: "Verifying", stepIndex: 2, stepCount: 2 }), t, noRetry);

		expect(row?.progress).toEqual({ kind: "indeterminate" });
		expect(row?.detail).toBe("Verifying the downloaded runtime… · Step 2 of 2");
	});

	it("keeps a failure with the server's reason and a retry for the failed status", () => {
		const failed = runtime({ sequence: 7, phase: "Failed", sanitizedError: "Offline." });
		const retry = vi.fn();
		const row = fromRuntimeAcquisition(failed, t, retry);

		expect(row).toMatchObject({ state: "failed", label: "The local AI runtime could not be installed", detail: "Offline." });
		row?.retry?.();
		expect(retry).toHaveBeenCalledWith(failed);
	});

	it("falls back to a generic reason when the failure carries none", () => {
		const row = fromRuntimeAcquisition(runtime({ phase: "Failed" }), t, noRetry);

		expect(row?.detail).toBe("The llama.cpp runtime could not be downloaded. Check your internet connection and try again.");
	});

	it("reports completion as succeeded", () => {
		expect(fromRuntimeAcquisition(runtime({ phase: "Completed" }), t, noRetry)).toMatchObject({
			state: "succeeded",
			label: "The local AI runtime is installed",
		});
	});
});

describe("operationTransitions", () => {
	function op(id: string, state: Operation["state"]): Operation {
		return { id, kind: "gguf-download", label: id, progress: { kind: "indeterminate" }, state, href: { to: "/models" } };
	}

	it("reports a running-to-terminal step once and nothing for a terminal state on first sight", () => {
		const first = operationTransitions(new Map(), [op("a", "running"), op("b", "succeeded")]);
		expect(first.finished).toEqual([]);

		const second = operationTransitions(first.seen, [op("a", "failed"), op("b", "succeeded")]);
		expect(second.finished.map((row) => row.id)).toEqual(["a"]);

		const replay = operationTransitions(second.seen, [op("a", "failed"), op("b", "succeeded")]);
		expect(replay.finished).toEqual([]);
	});

	it("reports a later run under the same id again, and forgets ids the sources dropped", () => {
		let seen = operationTransitions(new Map(), [op("rt", "running")]).seen;
		seen = operationTransitions(seen, [op("rt", "failed")]).seen;
		seen = operationTransitions(seen, [op("rt", "running")]).seen;

		const again = operationTransitions(seen, [op("rt", "failed")]);
		expect(again.finished.map((row) => row.id)).toEqual(["rt"]);
		expect([...operationTransitions(again.seen, []).seen]).toEqual([]);
	});
});

describe("waiting operations", () => {
	function op(id: string, state: Operation["state"]): Operation {
		return { id, kind: "graph-run", label: id, progress: { kind: "indeterminate" }, state, href: { to: "/models" } };
	}

	it("reports a wait on first sight, once, and never as finished", () => {
		const first = operationTransitions(new Map(), [op("w", "waiting")]);
		expect(first.waiting.map((row) => row.id)).toEqual(["w"]);

		const refetch = operationTransitions(first.seen, [op("w", "waiting")]);
		expect(refetch.waiting).toEqual([]);

		const decided = operationTransitions(refetch.seen, []);
		expect(decided.waiting).toEqual([]);
		expect(decided.finished).toEqual([]);
	});

	it("does not count a running row that starts waiting as finished, and reports the wait", () => {
		const running = operationTransitions(new Map(), [op("w", "running")]);
		const parked = operationTransitions(running.seen, [op("w", "waiting")]);

		expect(parked.finished).toEqual([]);
		expect(parked.waiting.map((row) => row.id)).toEqual(["w"]);
	});

	it("reports a new wait when the wait key changes under the same id", () => {
		const wait = (waitKey: string): Operation => ({ ...op("s", "waiting"), waitKey });
		const first = operationTransitions(new Map(), [wait("s:WaitingForInput")]);
		const second = operationTransitions(first.seen, [wait("s:WaitingForApproval")]);
		const repeat = operationTransitions(second.seen, [wait("s:WaitingForApproval")]);

		expect(first.waiting).toHaveLength(1);
		expect(second.waiting).toHaveLength(1);
		expect(repeat.waiting).toEqual([]);
	});

	it("keeps a waiting row active", () => {
		expect(isActiveOperation(op("w", "waiting"))).toBe(true);
	});
});
