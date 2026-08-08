// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";

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
	return render(<MantineProvider>{ui}</MantineProvider>);
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
		cpuFallbackReason: null,
		cpuFallbackRemediation: null,
		backendUndeterminedReason: null,
		gpuOffloadedLayers: null,
		gpuTotalLayers: null,
		gpuOffloadModelName: null,
		gpuOffloadRole: null,
		...overrides,
	};
}

function renderCard(overrides: Partial<HardwareProfile> = {}) {
	return renderWithProviders(
		<HardwareProfileCard profile={profile(overrides)} isLoading={false} isFetching={false} error={null} onRefresh={() => undefined} />,
	);
}

describe("HardwareProfileCard CPU-fallback alert (AUD4-20)", () => {
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

	it("renders a persistent alert with the reason and remediation when cpuFallback is true", () => {
		renderWithProviders(
			<HardwareProfileCard
				profile={profile({
					cpuFallback: true,
					cpuFallbackReason: "The Vulkan build found no usable GPU device.",
					cpuFallbackRemediation: "Install a CUDA runtime or build llama.cpp from source.",
				})}
				isLoading={false}
				isFetching={false}
				error={null}
				onRefresh={() => undefined}
			/>,
		);

		const alert = screen.getByTestId("model-fit-hardware-cpu-fallback-alert");
		expect(alert.textContent).toContain("The Vulkan build found no usable GPU device.");
		expect(screen.getByTestId("model-fit-hardware-cpu-fallback-remediation").textContent).toContain(
			"Install a CUDA runtime or build llama.cpp from source.",
		);
	});

	it("does not render the CPU-fallback alert when cpuFallback is false", () => {
		renderWithProviders(
			<HardwareProfileCard profile={profile({ cpuFallback: false })} isLoading={false} isFetching={false} error={null} onRefresh={() => undefined} />,
		);

		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-alert")).toBeNull();
	});

	it("omits the remediation line when the backend reports none", () => {
		renderWithProviders(
			<HardwareProfileCard
				profile={profile({ cpuFallback: true, cpuFallbackReason: "GPU unusable.", cpuFallbackRemediation: null })}
				isLoading={false}
				isFetching={false}
				error={null}
				onRefresh={() => undefined}
			/>,
		);

		expect(screen.getByTestId("model-fit-hardware-cpu-fallback-alert")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-remediation")).toBeNull();
	});

	it("resolves an ApiError with no server detail to the caller's fallback rather than an empty message", () => {
		// ApiError deliberately resolves to "" when the response carries no detail, so the unguarded
		// `error instanceof Error ? error.message : fallback` pattern renders an EMPTY alert.
		const detaillessApiError = new ApiError(500, { type: "", title: "", status: 500, detail: "" });
		expect(detaillessApiError.message).toBe("");

		renderWithProviders(
			<HardwareProfileCard profile={profile()} isLoading={false} isFetching={false} error={detaillessApiError} onRefresh={() => undefined} />,
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
		renderCard({ inferenceBackend: "cuda", cpuFallback: false, gpuOffloadedLayers: 38, gpuTotalLayers: 49, gpuOffloadModelName: "qwen3-14b" });

		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-alert")).toBeNull();
	});

	it("shows the count without a warning when every layer reached the GPU", () => {
		renderCard({ inferenceBackend: "cuda", gpuOffloadedLayers: 49, gpuTotalLayers: 49, gpuOffloadModelName: "qwen3-14b" });

		expect(screen.queryByTestId("model-fit-hardware-partial-offload-alert")).toBeNull();
		expect(screen.getByTestId("model-fit-hardware-gpu-layers").textContent).toBe("49 / 49");
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
		renderCard({
			inferenceBackend: "unknown",
			backendUndeterminedReason: "Listing GPU devices did not complete, so whether inference will use the GPU is unknown.",
		});

		const alert = screen.getByTestId("model-fit-hardware-backend-undetermined-alert");
		expect(alert.textContent).toContain("Listing GPU devices did not complete");
		// It is not a fallback claim: nobody proved the GPU is unused.
		expect(screen.queryByTestId("model-fit-hardware-cpu-fallback-alert")).toBeNull();
	});

	it("shows no undetermined alert when the backend is known", () => {
		renderCard({ inferenceBackend: "cuda" });

		expect(screen.queryByTestId("model-fit-hardware-backend-undetermined-alert")).toBeNull();
	});
});
