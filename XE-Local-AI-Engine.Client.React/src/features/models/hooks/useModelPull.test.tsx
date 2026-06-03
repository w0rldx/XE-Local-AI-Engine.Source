// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ModelPullProgressEvent } from "@/features/models/api/ModelPullStream";

// Mock the toast helper so the progress lifecycle is asserted at the call boundary (no real Mantine DOM toasts).
vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { progress: vi.fn(), success: vi.fn(), error: vi.fn() },
}));

// Deterministic i18n: t echoes the key (interpolation ignored) so toast assertions are stable without a provider.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key }),
}));

// The generated installed-models query key the hook invalidates on completion. Stable single-element key.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	listLocalModelsQueryKey: () => [{ _id: "listLocalModels" }],
}));

// Mock the hand-wired pull stream so the hook is driven with a deterministic, controllable event sequence — the
// test is independent of the real NDJSON/SSE transport.
const streamMock = vi.hoisted(() => ({ streamModelPull: vi.fn() }));
vi.mock("@/features/models/api/ModelPullStream", () => ({
	streamModelPull: streamMock.streamModelPull,
}));

import { toast } from "@/core/ui/notifications/Toast";
import { useModelPull } from "@/features/models/hooks/useModelPull";

// Builds an async iterable that yields the given events in order (one microtask apart), matching streamModelPull.
function asStream(events: readonly ModelPullProgressEvent[]): AsyncIterable<ModelPullProgressEvent> {
	return {
		async *[Symbol.asyncIterator]() {
			for (const event of events) {
				yield event;
			}
		},
	};
}

function renderUseModelPull() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	const view = renderHook(() => useModelPull(), { wrapper: Wrapper });
	return { ...view, invalidateSpy };
}

describe("useModelPull", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("opens a sticky progress toast, updates it per event with percent, finalizes success, and invalidates the installed-models query", async () => {
		streamMock.streamModelPull.mockReturnValue(
			asStream([
				{ status: "pulling manifest" },
				{ status: "downloading", completedBytes: 25, totalBytes: 100 },
				{ status: "downloading", completedBytes: 100, totalBytes: 100 },
			]),
		);

		const { result, invalidateSpy } = renderUseModelPull();

		act(() => {
			result.current.pull("orca-mini:latest");
		});

		// A sticky progress toast is opened immediately (the "preparing" call), then updated per event.
		await waitFor(() => expect(toast.success).toHaveBeenCalled());

		const progressCalls = (toast.progress as ReturnType<typeof vi.fn>).mock.calls;
		// First progress call opens the sticky toast before any stream event arrives.
		expect(progressCalls[0]?.[0]).toMatchObject({ id: "model-pull-orca-mini:latest" });
		// A mid-stream update carries the computed percent (25/100 -> 25).
		const withPercent = progressCalls.find((call) => call[0]?.percent === 25);
		expect(withPercent?.[0]).toMatchObject({ id: "model-pull-orca-mini:latest", percent: 25 });

		// Finalized with success keyed to the SAME id so it replaces (not stacks) the sticky toast.
		expect(toast.success).toHaveBeenCalledWith(
			expect.any(String),
			expect.objectContaining({ id: "model-pull-orca-mini:latest" }),
		);
		expect(toast.error).not.toHaveBeenCalled();

		// Authoritative installed-models list is invalidated on completion.
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: [{ _id: "listLocalModels" }] });

		// Hook returns to idle after the stream completes.
		await waitFor(() => expect(result.current.isPulling).toBe(false));
		expect(result.current.pullingModelName).toBeUndefined();
	});

	it("finalizes with a fixed i18n error toast (NOT the raw error.message) keyed to the same id and does not invalidate when the stream throws", async () => {
		// Silence the hook's diagnostic console.warn so it doesn't pollute test output.
		const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined);
		streamMock.streamModelPull.mockReturnValue({
			// biome-ignore lint/correctness/useYield: a stream that fails before its first event throws without yielding.
			async *[Symbol.asyncIterator]() {
				throw new Error("Pull stream request failed (500).");
			},
		} as AsyncIterable<ModelPullProgressEvent>);

		const { result, invalidateSpy } = renderUseModelPull();

		act(() => {
			result.current.pull("broken:latest");
		});

		await waitFor(() => expect(toast.error).toHaveBeenCalled());
		// The toast body is the fixed, model-scoped i18n key (t echoes keys in this mock) — NOT the raw thrown message.
		expect(toast.error).toHaveBeenCalledWith(
			"pages.models.pull.toast.error",
			expect.objectContaining({ id: "model-pull-broken:latest", title: "pages.models.pull.toast.errorTitle" }),
		);
		expect(toast.error).not.toHaveBeenCalledWith("Pull stream request failed (500).", expect.anything());
		expect(toast.success).not.toHaveBeenCalled();
		expect(invalidateSpy).not.toHaveBeenCalled();
		await waitFor(() => expect(result.current.isPulling).toBe(false));
		warnSpy.mockRestore();
	});

	it("invokes onSuccess once after a successful pull, and never invokes it when the stream throws", async () => {
		const onSuccess = vi.fn();

		streamMock.streamModelPull.mockReturnValue(asStream([{ status: "downloading", completedBytes: 100, totalBytes: 100 }]));
		const ok = renderUseModelPull();
		act(() => {
			ok.result.current.pull("orca-mini:latest", { onSuccess });
		});
		await waitFor(() => expect(onSuccess).toHaveBeenCalledTimes(1));

		// On failure the callback must NOT fire (the dialog stays open with the typed value for retry).
		const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined);
		const onSuccessFail = vi.fn();
		streamMock.streamModelPull.mockReturnValue({
			// biome-ignore lint/correctness/useYield: a stream that fails before its first event throws without yielding.
			async *[Symbol.asyncIterator]() {
				throw new Error("boom");
			},
		} as AsyncIterable<ModelPullProgressEvent>);
		const bad = renderUseModelPull();
		act(() => {
			bad.result.current.pull("broken:latest", { onSuccess: onSuccessFail });
		});
		await waitFor(() => expect(toast.error).toHaveBeenCalled());
		expect(onSuccessFail).not.toHaveBeenCalled();
		warnSpy.mockRestore();
	});

	it("throttles a burst of same-status byte updates to a single progress render while still rendering each phase change", async () => {
		// A realistic Ollama burst: one phase change into "downloading" followed by many same-status byte updates
		// arriving in the same tick. Only the first "downloading" (the phase change) should reach the toast; the rest
		// are coalesced by the throttle so React/Mantine is not flooded.
		streamMock.streamModelPull.mockReturnValue(
			asStream([
				{ status: "pulling manifest" },
				{ status: "downloading", completedBytes: 10, totalBytes: 100 },
				{ status: "downloading", completedBytes: 20, totalBytes: 100 },
				{ status: "downloading", completedBytes: 30, totalBytes: 100 },
				{ status: "downloading", completedBytes: 40, totalBytes: 100 },
				{ status: "verifying", completedBytes: 100, totalBytes: 100 },
			]),
		);

		const { result } = renderUseModelPull();
		act(() => {
			result.current.pull("orca-mini:latest");
		});
		await waitFor(() => expect(toast.success).toHaveBeenCalled());

		const progressCalls = (toast.progress as ReturnType<typeof vi.fn>).mock.calls;
		const statuses = progressCalls.map((call) => call[0]?.message);
		// Opening "preparing" + phase changes "pulling manifest", "downloading", "verifying". The 3 extra same-status
		// "downloading" byte updates (20/30/40) are throttled away — they would otherwise be 3 more renders.
		const downloadingCount = statuses.filter((s) => s === "downloading").length;
		expect(downloadingCount).toBe(1);
		expect(statuses).toContain("pulling manifest");
		expect(statuses).toContain("verifying");
	});

	it("ignores an empty/whitespace model name and never starts a stream", () => {
		streamMock.streamModelPull.mockReturnValue(asStream([]));
		const { result } = renderUseModelPull();

		act(() => {
			result.current.pull("   ");
		});

		expect(streamMock.streamModelPull).not.toHaveBeenCalled();
		expect(result.current.isPulling).toBe(false);
	});
});
