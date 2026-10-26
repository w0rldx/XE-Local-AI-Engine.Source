// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

import { CpuFallbackBanner } from "@/features/model-fit/components/CpuFallbackBanner";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

const hardware: { data: Partial<HardwareProfile> | undefined } = { data: undefined };
vi.mock("@/features/model-fit/queries/useModelFit", () => ({ useHardwareProfile: () => hardware }));

function renderBanner(profile: Partial<HardwareProfile>) {
	hardware.data = profile;
	return renderWithMantine(<CpuFallbackBanner />);
}

beforeAll(installJsdomEnvironmentMocks);
afterEach(cleanup);

describe("CpuFallbackBanner", () => {
	// The server's reason is English; the banner leads with the localized sentence and keeps the server text as detail.
	it("leads a CPU fallback with the localized sentence and keeps the server text as detail", () => {
		renderBanner({
			cpuFallback: true,
			gpuAccelAvailable: true,
			cpuFallbackReason: "The Vulkan build found no usable GPU device.",
			cpuFallbackRemediation: "Install a CUDA runtime.",
		});

		expect(screen.getByTestId("cpu-fallback-banner").textContent).toContain(
			"The selected inference runtime could not use this machine's GPU",
		);
		expect(screen.getByTestId("cpu-fallback-banner-detail").textContent).toBe(
			"The Vulkan build found no usable GPU device. Install a CUDA runtime.",
		);
	});

	it("shows only the localized sentence when no supported GPU exists", () => {
		renderBanner({ cpuFallback: false, gpuAccelAvailable: false, cpuFallbackReason: null, cpuFallbackRemediation: null });

		expect(screen.getByTestId("cpu-fallback-banner").textContent).toContain("No supported GPU detected");
		expect(screen.queryByTestId("cpu-fallback-banner-detail")).toBeNull();
	});
});
