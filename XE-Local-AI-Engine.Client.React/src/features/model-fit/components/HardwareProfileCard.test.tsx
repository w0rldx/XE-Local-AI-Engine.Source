// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";
import { testMantineTheme } from "@/test/MantineTestRender";

// Stands in for i18next including its {{placeholder}} interpolation, so assertions can read the rendered sentence
// rather than an uninterpolated template. Mirrors the two call shapes the card uses: (key, default) and
// (key, { defaultValue, ...values }).
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, second?: string | Record<string, unknown>) => {
			if (typeof second === "string" || second === undefined) {
				return second ?? key;
			}

			const template = typeof second["defaultValue"] === "string" ? second["defaultValue"] : key;
			return template.replace(/{{(\w+)}}/g, (match, name: string) => (name in second ? String(second[name]) : match));
		},
	}),
}));

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
}

function profile(overrides: Partial<HardwareProfile> = {}): HardwareProfile {
	return {
		totalRamBytes: 32 * 1024 ** 3,
		availableRamBytes: 24 * 1024 ** 3,
		vramBytes: 16 * 1024 ** 3,
		vramKnown: true,
		gpuVendor: "nvidia",
		gpuAccelAvailable: true,
		cpuCores: 16,
		freeDiskBytes: 512 * 1024 ** 3,
		inferenceBackend: "cuda",
		gpuExpected: true,
		cpuFallback: false,
		cpuFallbackReasonCode: null,
		cpuFallbackRemediationCode: null,
		backendUndeterminedReasonCode: null,
		gpuOffloadedLayers: null,
		gpuTotalLayers: null,
		gpuExpertsOffloaded: false,
		gpuOffloadModelName: null,
		gpuOffloadRole: null,
		...overrides,
	};
}

function renderCard(overrides: Partial<HardwareProfile> = {}) {
	return renderWithProviders(
		<HardwareProfileCard
			profile={profile(overrides)}
			isLoading={false}
			isFetching={false}
			error={null}
			onRefresh={() => undefined}
		/>,
	);
}

describe("HardwareProfileCard CPU-fallback alert", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a persistent alert with the reason and remediation its codes select when cpuFallback is true", () => {
		renderWithProviders(
			<HardwareProfileCard
				profile={profile({
					cpuFallback: true,
					cpuFallbackReasonCode: "zero_devices",
					cpuFallbackRemediationCode: "linux_other",
				})}
				isLoading={false}
				isFetching={false}
				error={null}
				onRefresh={() => undefined}
			/>,
		);

		expect(screen.getByTestId("model-fit-hardware-cpu-fallback-alert")).toBeTruthy();
		expect(screen.getByTestId("model-fit-hardware-cpu-fallback-reason").textContent).toBe(
			"The selected llama.cpp GPU runtime found no GPU devices, so inference is running on the CPU.",
		);
		expect(screen.getByTestId("model-fit-hardware-cpu-fallback-remediation").textContent).toContain(
			"In Node Settings, use automatic variant selection if a variant was pinned",
		);
	});

	it("does not render the CPU-fallback alert when cpuFallback is false", () => {
		renderWithProviders(
			<HardwareProfileCard
				profile={profile({ cpuFallback: false })}
				isLoading={false}
				isFetching={false}
				error={null}
				onRefresh={() => undefined}
			/>,
		);

		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-alert")).toBeNull();
	});

	it("falls back to the generic sentence and omits the remediation for codes this client does not know", () => {
		renderWithProviders(
			<HardwareProfileCard
				profile={profile({ cpuFallback: true, cpuFallbackReasonCode: "future_reason", cpuFallbackRemediationCode: null })}
				isLoading={false}
				isFetching={false}
				error={null}
				onRefresh={() => undefined}
			/>,
		);

		expect(screen.getByTestId("model-fit-hardware-cpu-fallback-reason").textContent).toBe(
			"The selected inference runtime could not use this machine's GPU, so inference is running on the CPU.",
		);
		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-remediation")).toBeNull();
	});

	it("resolves an ApiError with no server detail to the caller's fallback rather than an empty message", () => {
		// ApiError deliberately resolves to "" when the response carries no detail, so the unguarded
		// `error instanceof Error ? error.message : fallback` pattern renders an EMPTY alert.
		const detaillessApiError = new ApiError(500, { type: "", title: "", status: 500, detail: "" });
		expect(detaillessApiError.message).toBe("");

		renderWithProviders(
			<HardwareProfileCard
				profile={profile()}
				isLoading={false}
				isFetching={false}
				error={detaillessApiError}
				onRefresh={() => undefined}
			/>,
		);

		expect(screen.getByTestId("model-fit-hardware-error").textContent).toBe("Could not detect hardware.");
	});
});

describe("HardwareProfileCard layer placement", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("warns, naming the model and the shortfall, when only some layers reached the GPU", () => {
		renderCard({
			inferenceBackend: "cuda",
			gpuOffloadedLayers: 38,
			gpuTotalLayers: 49,
			gpuOffloadModelName: "qwen3-14b",
			gpuOffloadRole: "chat",
		});

		const alert = screen.getByTestId("model-fit-hardware-partial-offload-alert");
		expect(alert.textContent).toContain("38");
		expect(alert.textContent).toContain("49");
		expect(alert.textContent).toContain("qwen3-14b");
		// The remainder is spelled out so the operator does not have to subtract.
		expect(alert.textContent).toContain("11");
		expect(screen.getByTestId("model-fit-hardware-gpu-layers").textContent).toBe("38 / 49");
	});

	it("keeps a partial offload out of the CPU-fallback alert — the GPU is in use", () => {
		renderCard({
			inferenceBackend: "cuda",
			cpuFallback: false,
			gpuOffloadedLayers: 38,
			gpuTotalLayers: 49,
			gpuOffloadModelName: "qwen3-14b",
		});

		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-alert")).toBeNull();
	});

	it("shows the count without a warning when every layer reached the GPU", () => {
		renderCard({ inferenceBackend: "cuda", gpuOffloadedLayers: 49, gpuTotalLayers: 49, gpuOffloadModelName: "qwen3-14b" });

		expect(screen.queryByTestId("model-fit-hardware-partial-offload-alert")).toBeNull();
		expect(screen.queryByTestId("model-fit-hardware-expert-offload-alert")).toBeNull();
		expect(screen.getByTestId("model-fit-hardware-gpu-layers").textContent).toBe("49 / 49");
	});

	it("does not present an expert-offloaded model as fully on the GPU", () => {
		// llama.cpp reports 41/41 for a --cpu-moe launch that keeps 20.7 GB of experts in system RAM.
		renderCard({
			inferenceBackend: "cuda",
			gpuOffloadedLayers: 41,
			gpuTotalLayers: 41,
			gpuExpertsOffloaded: true,
			gpuOffloadModelName: "qwen3.6-35b-a3b",
		});

		const alert = screen.getByTestId("model-fit-hardware-expert-offload-alert");
		expect(alert.textContent).toContain("qwen3.6-35b-a3b");
		expect(alert.textContent).toContain("system RAM");
		expect(screen.getByTestId("model-fit-hardware-gpu-layers").textContent).toBe("41 / 41, experts in RAM");
		expect(screen.queryByTestId("model-fit-hardware-partial-offload-alert")).toBeNull();
	});

	it("reads 'not measured yet' before any model has loaded, never a zero", () => {
		renderCard();

		expect(screen.getByTestId("model-fit-hardware-gpu-layers").textContent).toBe("Not measured yet");
		expect(screen.queryByTestId("model-fit-hardware-partial-offload-alert")).toBeNull();
	});

	it("ignores a half-populated placement payload instead of rendering a nonsense count", () => {
		renderCard({ gpuOffloadedLayers: 38, gpuTotalLayers: null });

		expect(screen.getByTestId("model-fit-hardware-gpu-layers").textContent).toBe("Not measured yet");
		expect(screen.queryByTestId("model-fit-hardware-partial-offload-alert")).toBeNull();
	});
});

describe("HardwareProfileCard undetermined backend", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("surfaces an undetermined backend instead of showing nothing at all", () => {
		// The regression this covers: a wedged driver or an overrun device probe leaves the backend "unknown" with
		// cpuFallback false, which previously rendered no indication of any kind.
		renderCard({ inferenceBackend: "unknown", backendUndeterminedReasonCode: "probe_failed" });

		const alert = screen.getByTestId("model-fit-hardware-backend-undetermined-alert");
		expect(alert.textContent).toContain("The GPU devices of the selected runtime have not been listed");
		// It is not a fallback claim: nobody proved the GPU is unused.
		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-alert")).toBeNull();
	});

	// The server's undetermined reason is English; the detail line is the localized text its code selects.
	it.each([
		["runtime_missing", "No llama.cpp runtime is installed yet."],
		["companion_libraries_incomplete", "some of its CUDA companion libraries are missing"],
		["probe_failed", "Refreshing the hardware profile re-runs the probe."],
	])("localizes the %s detail from its code", (code, detail) => {
		renderCard({ inferenceBackend: "unknown", backendUndeterminedReasonCode: code });

		expect(screen.getByTestId("model-fit-hardware-backend-undetermined-detail").textContent).toContain(detail);
	});

	it("keeps only the generic sentence for an undetermined code this client does not know", () => {
		renderCard({ inferenceBackend: "unknown", backendUndeterminedReasonCode: "future_cause" });

		expect(screen.getByTestId("model-fit-hardware-backend-undetermined-alert").textContent).toContain(
			"The GPU devices of the selected runtime have not been listed",
		);
		expect(screen.queryByTestId("model-fit-hardware-backend-undetermined-detail")).toBeNull();
	});

	it("shows no undetermined alert when the backend is known", () => {
		renderCard({ inferenceBackend: "cuda" });

		expect(screen.queryByTestId("model-fit-hardware-backend-undetermined-alert")).toBeNull();
	});
});
