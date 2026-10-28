// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, fallback?: string) => fallback ?? _key,
	}),
}));

import type { XeLocalAiEngineClientEndpointsDevelopmentV1SandboxIsolationSummaryResponse as SandboxIsolation } from "@/core/api/generated/types.gen";
import { DevelopmentFilesystemBoundaryNotice } from "@/features/development/components/DevelopmentFilesystemBoundaryNotice";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

function row(overrides: Partial<SandboxIsolation>): SandboxIsolation {
	return {
		role: "development",
		provider: "process",
		backend: "process",
		level: "Confined",
		maturity: "Stable",
		filesystemIsolation: false,
		networkIsolation: true,
		resourceLimits: true,
		readOnlyMounts: false,
		filesystemIsolationRequired: false,
		networkIsolationRequired: false,
		resourceLimitsRequired: false,
		satisfied: true,
		filesystemIsolationUnavailableReason: "bwrap is not installed",
		resourceLimitsUnavailableReason: null,
		networkIsolationUnavailableReason: null,
		...overrides,
	};
}

function renderNotice(roles: readonly SandboxIsolation[] | undefined) {
	render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<DevelopmentFilesystemBoundaryNotice roles={roles} />
		</MantineProvider>,
	);
}

describe("DevelopmentFilesystemBoundaryNotice", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("warns that the coder can write outside its workspace, with the measured reason, where Development runs without the boundary", () => {
		renderNotice([row({})]);

		expect(screen.getByTestId("development-no-filesystem-boundary").textContent).toContain("can write outside its workspace");
		expect(screen.getByTestId("development-no-filesystem-boundary-reason").textContent).toBe("bwrap is not installed");
	});

	it.each([
		["the boundary is served", row({ filesystemIsolation: true, filesystemIsolationUnavailableReason: null })],
		["high refuses the role instead of running it", row({ filesystemIsolationRequired: true, satisfied: false })],
		["a container has the boundary by construction", row({ provider: "docker", backend: "docker" })],
		["the row is another role's", row({ role: "agent-home" })],
	])("stays silent when %s", (_case, role) => {
		renderNotice([role]);

		expect(screen.queryByTestId("development-no-filesystem-boundary")).toBeNull();
	});

	it("renders nothing while the capability is unknown", () => {
		renderNotice(undefined);

		expect(screen.queryByTestId("development-no-filesystem-boundary")).toBeNull();
	});
});
