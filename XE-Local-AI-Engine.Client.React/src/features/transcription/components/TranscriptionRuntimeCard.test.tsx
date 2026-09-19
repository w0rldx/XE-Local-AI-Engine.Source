// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// A node ships with zero whisper weights and nothing fetches one implicitly, so this card is the ONLY path from a
// fresh install to a working transcription. What is pinned here is that path end to end: the first-run alert names
// the model the node's own hardware recommends, Download reaches the endpoint AND leaves the row showing progress
// (the refetch is what arms the five-second poll — without it the operator clicks and watches nothing happen),
// Cancel and Use-this-model reach theirs, a failed transfer shows the coordinator's sanitized reason with a retry,
// and a refused select is reported instead of swallowed.

const { toastMock } = vi.hoisted(() => ({
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { TranscriptionRuntimeCard } from "@/features/transcription/components/TranscriptionRuntimeCard";

setupMswServer();

const copy = en.pages.transcription.runtime.models;

interface ModelOverrides {
	readonly installed?: boolean;
	readonly download?: {
		readonly phase: string;
		readonly completedBytes?: number;
		readonly totalBytes?: number;
		readonly sanitizedError?: string;
	} | null;
}

function model(id: string, sizeBytes: number, overrides: ModelOverrides = {}) {
	return {
		id,
		tier: "Tiny",
		sizeBytes,
		approximateVramBytes: 400 * 1024 * 1024,
		approximateRamBytes: 400 * 1024 * 1024,
		englishOnly: false,
		installed: overrides.installed ?? false,
		download: overrides.download ?? null,
	};
}

function modelList(models: readonly unknown[], selectedModelId: string | null = null, recommendedModelId = "base") {
	return { models, selectedModelId, recommendedModelId };
}

function runtimeBody(overrides: { selectedModelId?: string | null; vadInstalled?: boolean } = {}) {
	return {
		enabled: true,
		state: "stopped",
		backend: null,
		binarySource: null,
		binaryVersion: null,
		loadedModelId: null,
		selectedModelId: overrides.selectedModelId ?? null,
		recommendedModelId: "base",
		supportsTranscode: true,
		idleTimeoutMinutes: 10,
		vadInstalled: overrides.vadInstalled ?? false,
		processCaptureSupported: false,
		managedRuntime: null,
		activity: {
			activeTranscriptionCount: 0,
			spawnReadinessCount: 0,
			residentProcessCount: 0,
			mutationReserved: false,
			evictionReserved: false,
			isBusy: false,
		},
	};
}

function runtimeRoute(selectedModelId: string | null = null) {
	return jsonRoute("get", "transcription/runtime", runtimeBody({ selectedModelId }));
}

function recommendationRoute(backend: "cpu" | "cuda" = "cpu") {
	return jsonRoute("get", "transcription/runtime/recommendation", {
		recommendedModelId: "base",
		tier: "Base",
		approximateVramBytes: 838_860_800,
		approximateRamBytes: 576_716_800,
		backend,
	});
}

describe("TranscriptionRuntimeCard", () => {
	beforeEach(() => {
		toastMock.error.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	// The defect this whole card exists for: on a fresh node nothing is installed, and every other surface reports
	// that only as a raw runtime exception after the operator has already uploaded a recording.
	it("tells a fresh node that transcription needs a model and offers the recommended one", async () => {
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			jsonRoute("get", "transcription/models", modelList([model("tiny", 77_691_713), model("base", 147_951_465)])),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		const empty = await screen.findByTestId("transcription-models-empty");
		expect(empty.textContent).toContain("Transcription needs a model on this node");
		expect(empty.textContent).toContain("base");
		expect(screen.getByTestId("transcription-models-empty-download").textContent).toBe(
			copy.downloadRecommended.replace("{{model}}", "base"),
		);
	});

	// The hint has to say WHY, from the endpoint's own figures — the backend it was sized for and the resident
	// footprint — or "recommended" is an unexplained pick the operator has no reason to trust.
	it("explains the recommendation with the backend and footprint the node reported", async () => {
		server.use(
			runtimeRoute(),
			recommendationRoute("cuda"),
			jsonRoute("get", "transcription/models", modelList([model("base", 147_951_465, { installed: true })])),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		const hint = await screen.findByTestId("transcription-models-recommendation");
		expect(hint.textContent).toContain("Recommended for this computer: base");
		// The CUDA figure, not the RAM one: 838_860_800 bytes renders as 800 MB.
		expect(hint.textContent).toContain("800 MB");
		expect(hint.textContent).toContain("CUDA");
	});

	// Start is a 202 that never reports the outcome, so the mutation MUST invalidate the catalogue: the refetched
	// running row is both what the operator sees and what arms the five-second poll.
	it("starts a download and shows the row's progress once the catalogue is re-read", async () => {
		let started: unknown = null;
		let listReads = 0;
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			http.get(localApiPath("transcription/models"), () => {
				listReads += 1;
				return HttpResponse.json(
					listReads === 1
						? modelList([model("tiny", 77_691_713)])
						: modelList([
								model("tiny", 77_691_713, {
									download: { phase: "running", completedBytes: 38_845_856, totalBytes: 77_691_713 },
								}),
							]),
				);
			}),
			http.post(localApiPath("transcription/models/downloads"), async ({ request }) => {
				started = await request.json();
				return HttpResponse.json({ modelId: "tiny", accepted: true, alreadyInFlight: false }, { status: 202 });
			}),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		fireEvent.click(await screen.findByTestId("transcription-model-download-tiny"));

		await waitFor(() => {
			expect(started).toEqual({ modelId: "tiny" });
		});
		expect(await screen.findByTestId("transcription-model-progress-tiny")).toBeDefined();
		const row = screen.getByTestId("transcription-model-row-tiny");
		expect(row.textContent).toContain(copy.progress.replace("{{percent}}", "50"));
	});

	// A 1.6 GB pull that cannot be stopped holds the node's bandwidth and disk until it finishes, so a running row
	// offers Cancel and nothing else.
	it("cancels an in-flight download through the cancel endpoint", async () => {
		let cancelled: unknown = null;
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			jsonRoute(
				"get",
				"transcription/models",
				modelList([model("tiny", 77_691_713, { download: { phase: "running", completedBytes: 1000, totalBytes: 77_691_713 } })]),
			),
			http.post(localApiPath("transcription/models/downloads/cancel"), async ({ request }) => {
				cancelled = await request.json();
				return HttpResponse.json({ modelId: "tiny", accepted: true });
			}),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		fireEvent.click(await screen.findByTestId("transcription-model-cancel-tiny"));

		await waitFor(() => {
			expect(cancelled).toEqual({ modelId: "tiny" });
		});
		expect(screen.queryByTestId("transcription-model-download-tiny")).toBeNull();
	});

	it("pins an installed model through the select endpoint", async () => {
		let selected: unknown = null;
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			jsonRoute(
				"get",
				"transcription/models",
				modelList([model("tiny", 77_691_713, { installed: true }), model("base", 147_951_465, { installed: true })]),
			),
			http.post(localApiPath("transcription/models/select"), async ({ request }) => {
				selected = await request.json();
				return HttpResponse.json(modelList([model("tiny", 77_691_713, { installed: true })], "tiny"));
			}),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		fireEvent.click(await screen.findByTestId("transcription-model-select-tiny"));

		await waitFor(() => {
			expect(selected).toEqual({ modelId: "tiny" });
		});
	});

	// Which model is in use and which the hardware suggests are two different facts, and the operator acts on both:
	// one says "this is what runs", the other says "this is what would run better here".
	it("marks the selected and the recommended rows distinctly", async () => {
		server.use(
			runtimeRoute("tiny"),
			recommendationRoute(),
			jsonRoute(
				"get",
				"transcription/models",
				modelList([model("tiny", 77_691_713, { installed: true }), model("base", 147_951_465, { installed: true })], "tiny"),
			),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		expect((await screen.findByTestId("transcription-model-selected-tiny")).textContent).toBe(copy.selected);
		expect(screen.getByTestId("transcription-model-recommended-base").textContent).toBe(copy.recommended);
		// The row already in use offers no "use this model"; the other one does.
		expect(screen.queryByTestId("transcription-model-select-tiny")).toBeNull();
		expect(screen.getByTestId("transcription-model-select-base")).toBeDefined();
	});

	// The transfer outlives the request that started it, so its failure is only ever observable on the row. A row
	// that just went quiet would leave the operator waiting for a download that died.
	it("reports a failed download with the node's own reason and offers a retry", async () => {
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			jsonRoute(
				"get",
				"transcription/models",
				modelList([model("tiny", 77_691_713, { download: { phase: "failed", sanitizedError: "the checksum did not match" } })]),
			),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		const failure = await screen.findByTestId("transcription-model-failed-tiny");
		expect(failure.textContent).toBe(copy.downloadFailedReason.replace("{{reason}}", "the checksum did not match"));
		expect(screen.getByTestId("transcription-model-download-tiny").textContent).toBe(copy.retry);
	});

	// The select endpoint answers 400 for an id outside the catalogue. Swallowing it would leave the row looking
	// unchanged with no explanation of why the pick did not stick.
	it("surfaces the node's refusal when a model cannot be selected", async () => {
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			jsonRoute("get", "transcription/models", modelList([model("tiny", 77_691_713, { installed: true })])),
			domainErrorRoute("post", "transcription/models/select", 400, {
				detail: "The requested transcription model is not in the catalogue.",
			}),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		fireEvent.click(await screen.findByTestId("transcription-model-select-tiny"));

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalledWith("The requested transcription model is not in the catalogue.");
		});
	});

	// Part one of EVERY model download is the Silero VAD file, so the runtime card's "voice-activity detection is not
	// installed" line becomes false the moment a transfer lands. Nothing observes that: the transfer outlives the
	// request that started it, and only the five-second list poll sees it stop. This pins the edge the fix hangs on —
	// running → not running re-reads the runtime status exactly once — with fake timers rather than a sleep.
	it("re-reads the runtime status when a transfer stops, so the VAD line stops lying", async () => {
		let modelReads = 0;
		let runtimeReads = 0;
		server.use(
			http.get(localApiPath("transcription/runtime"), () => {
				runtimeReads += 1;
				return HttpResponse.json(runtimeBody({ vadInstalled: runtimeReads > 1 }));
			}),
			recommendationRoute(),
			http.get(localApiPath("transcription/models"), () => {
				modelReads += 1;
				return HttpResponse.json(
					modelReads === 1
						? modelList([
								model("tiny", 77_691_713, {
									download: { phase: "running", completedBytes: 70_000_000, totalBytes: 77_691_713 },
								}),
							])
						: modelList([model("tiny", 77_691_713, { installed: true, download: { phase: "completed" } })]),
				);
			}),
		);
		// `shouldAdvanceTime` keeps the MSW round-trips resolving while the clock is under the test's control; without
		// it the refetch this drives would await a promise that the frozen clock never lets settle.
		vi.useFakeTimers({ shouldAdvanceTime: true });
		try {
			renderWithProviders(<TranscriptionRuntimeCard />);

			const card = await screen.findByTestId("transcription-runtime-card");
			await waitFor(() => {
				expect(card.textContent).toContain(en.pages.transcription.runtime.vadMissing);
			});

			// The catalogue's own five-second poll — the only observer of a finished transfer.
			await vi.advanceTimersByTimeAsync(5_000);

			await waitFor(() => {
				expect(card.textContent).toContain(en.pages.transcription.runtime.vadInstalled);
			});
			expect(runtimeReads).toBeGreaterThan(1);
		} finally {
			vi.useRealTimers();
		}
	});

	// A pin has to be reversible, and `SelectTranscriptionModelRequest.modelId` documents null as the clear — without
	// this control an operator who once pinned a model can never get back to "follow this node's hardware".
	it("offers the clear-pin control only while a model is pinned", async () => {
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			jsonRoute("get", "transcription/models", modelList([model("tiny", 77_691_713, { installed: true })])),
		);
		const { unmount } = renderWithProviders(<TranscriptionRuntimeCard />);

		await screen.findByTestId("transcription-model-row-tiny");
		expect(screen.queryByTestId("transcription-models-clear-selection")).toBeNull();
		unmount();

		server.use(
			runtimeRoute("tiny"),
			jsonRoute("get", "transcription/models", modelList([model("tiny", 77_691_713, { installed: true })], "tiny")),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		expect((await screen.findByTestId("transcription-models-clear-selection")).textContent).toBe(copy.clearSelection);
	});

	it("clears the pin by selecting a null model id", async () => {
		let selected: unknown = "never called";
		server.use(
			runtimeRoute("tiny"),
			recommendationRoute(),
			jsonRoute("get", "transcription/models", modelList([model("tiny", 77_691_713, { installed: true })], "tiny")),
			http.post(localApiPath("transcription/models/select"), async ({ request }) => {
				selected = await request.json();
				return HttpResponse.json(modelList([model("tiny", 77_691_713, { installed: true })]));
			}),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		fireEvent.click(await screen.findByTestId("transcription-models-clear-selection"));

		await waitFor(() => {
			expect(selected).toEqual({ modelId: null });
		});
	});

	it("surfaces the node's refusal when the pin cannot be cleared", async () => {
		server.use(
			runtimeRoute("tiny"),
			recommendationRoute(),
			jsonRoute("get", "transcription/models", modelList([model("tiny", 77_691_713, { installed: true })], "tiny")),
			domainErrorRoute("post", "transcription/models/select", 500, { detail: "the node settings file is read-only" }),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		fireEvent.click(await screen.findByTestId("transcription-models-clear-selection"));

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalledWith("the node settings file is read-only");
		});
	});

	// The catalogue is a separate read from the runtime status: a card that rendered its status but silently no
	// model list would look complete while offering no way to install anything.
	it("reports a catalogue that could not be read", async () => {
		server.use(
			runtimeRoute(),
			recommendationRoute(),
			domainErrorRoute("get", "transcription/models", 500, { detail: "the catalogue is unavailable" }),
		);
		renderWithProviders(<TranscriptionRuntimeCard />);

		expect((await screen.findByTestId("transcription-models-error")).textContent).toContain("the catalogue is unavailable");
	});
});
