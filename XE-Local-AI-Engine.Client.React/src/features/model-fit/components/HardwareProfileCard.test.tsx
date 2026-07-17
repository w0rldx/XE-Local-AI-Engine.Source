// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string) => defaultValue ?? _key,
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
		...overrides,
	};
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
});
