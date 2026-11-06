// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

import { CpuFallbackBanner } from "@/features/model-fit/components/CpuFallbackBanner";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const hardware: { data: Partial<HardwareProfile> | undefined } = { data: undefined };
vi.mock("@/features/model-fit/queries/useModelFit", () => ({ useHardwareProfile: () => hardware }));

function renderBanner(profile: Partial<HardwareProfile>) {
	hardware.data = profile;
	return renderWithMantine(<CpuFallbackBanner />);
}

beforeAll(installJsdomEnvironmentMocks);
afterEach(cleanup);

describe("CpuFallbackBanner", () => {
	// The server's reason and remediation are English; the banner renders only the bundle text its codes select.
	it.each([
		[
			"cpu_variant",
			"windows",
			"A GPU was detected, but the CPU llama.cpp runtime is selected, so inference is running on the CPU.",
			"Update the GPU driver, check the GPU is visible to the system, then reinstall the runtime from Node Settings.",
		],
		[
			"zero_devices",
			"linux_nvidia",
			"The selected llama.cpp GPU runtime found no GPU devices, so inference is running on the CPU.",
			"In Node Settings, use automatic variant selection (or install the CUDA variant)",
		],
		[
			"zero_devices",
			"linux_other",
			"The selected llama.cpp GPU runtime found no GPU devices, so inference is running on the CPU.",
			"In Node Settings, use automatic variant selection if a variant was pinned",
		],
	])("localizes a %s fallback with the %s remediation from its codes", (reasonCode, remediationCode, reason, remediation) => {
		renderBanner({
			cpuFallback: true,
			gpuAccelAvailable: true,
			cpuFallbackReasonCode: reasonCode,
			cpuFallbackRemediationCode: remediationCode,
		});

		expect(screen.getByTestId("cpu-fallback-banner").textContent).toContain(reason);
		expect(screen.getByTestId("cpu-fallback-banner-detail").textContent).toContain(remediation);
	});

	it("shows the generic sentence and no remediation for a code this client does not know", () => {
		renderBanner({
			cpuFallback: true,
			gpuAccelAvailable: true,
			cpuFallbackReasonCode: "future_reason",
			cpuFallbackRemediationCode: "future_os",
		});

		expect(screen.getByTestId("cpu-fallback-banner").textContent).toContain(
			"The selected inference runtime could not use this machine's GPU, so inference is running on the CPU.",
		);
		expect(screen.queryByTestId("cpu-fallback-banner-detail")).toBeNull();
	});

	it("shows only the localized sentence when no supported GPU exists", () => {
		renderBanner({ cpuFallback: false, gpuAccelAvailable: false, cpuFallbackReasonCode: null, cpuFallbackRemediationCode: null });

		expect(screen.getByTestId("cpu-fallback-banner").textContent).toContain("No supported GPU detected");
		expect(screen.queryByTestId("cpu-fallback-banner-detail")).toBeNull();
	});
});
