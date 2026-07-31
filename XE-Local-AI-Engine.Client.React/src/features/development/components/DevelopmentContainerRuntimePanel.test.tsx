// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

import { DevelopmentContainerRuntimePanel } from "@/features/development/components/DevelopmentContainerRuntimePanel";
import type { XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentContainerRuntimeResponse as ContainerRuntimeStatus } from "@/core/api/generated/types.gen";

function renderPanel(
	runtime: ContainerRuntimeStatus | undefined,
	onConfirm = vi.fn(),
	confirmError?: string,
	sandboxProvider?: string,
) {
	render(
		<MantineProvider>
			<DevelopmentContainerRuntimePanel
				runtime={runtime}
				onConfirm={onConfirm}
				confirming={false}
				confirmError={confirmError}
				sandboxProvider={sandboxProvider}
			/>
		</MantineProvider>,
	);
	return onConfirm;
}

// Mantine's provider reads the color scheme from matchMedia, which jsdom does not implement. Same shim the
// DevelopmentPage suite installs.
beforeEach(() => {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
		})),
	});
});

afterEach(cleanup);

describe("DevelopmentContainerRuntimePanel", () => {
	it("renders the exact status code and message for an unreachable daemon", () => {
		// Values, not containers. Asserting only that the panel rendered would pass against an empty message and the
		// wrong status — which is the whole failure this surface exists to make visible.
		renderPanel({
			ready: false,
			status: "daemon_unreachable",
			message: "Development Mode needs a running container runtime and could not reach one at unix:///var/run/docker.sock.",
			requiresOperatorConfirmation: false,
			endpoint: "unix:///var/run/docker.sock",
		});

		expect(screen.getByTestId("development-container-runtime-status").textContent).toBe("daemon_unreachable");
		expect(screen.getByTestId("development-container-runtime-message").textContent).toContain(
			"needs a running container runtime",
		);
		expect(screen.getByTestId("development-container-runtime-endpoint").textContent).toBe("unix:///var/run/docker.sock");
		expect(screen.queryByTestId("development-container-runtime-confirm")).toBeNull();
	});

	it("renders the ready status and does not offer a confirmation", () => {
		renderPanel({
			ready: true,
			status: "ready",
			message: "Container runtime ready: Docker Engine 29.6.1 (API 1.55).",
			requiresOperatorConfirmation: false,
			observedDaemon: { daemonId: "daemon-alpha", serverVersion: "29.6.1", endpoint: "unix:///var/run/docker.sock" },
		});

		expect(screen.getByTestId("development-container-runtime-status").textContent).toBe("ready");
		expect(screen.getByTestId("development-container-runtime-message").textContent).toContain("29.6.1");
		expect(screen.queryByTestId("development-container-runtime-confirm")).toBeNull();
	});

	it("shows both daemon identities and confirms with the one the operator was shown", () => {
		const onConfirm = renderPanel({
			ready: false,
			status: "daemon_changed",
			message: "Development Mode is pinned to a different container runtime than the one it can reach now.",
			requiresOperatorConfirmation: true,
			endpoint: "unix:///run/user/1000/docker.sock",
			pinnedDaemon: { daemonId: "daemon-alpha", serverVersion: "29.6.1", endpoint: "unix:///var/run/docker.sock" },
			observedDaemon: { daemonId: "daemon-beta", serverVersion: "28.0.0", endpoint: "unix:///run/user/1000/docker.sock" },
		});

		expect(screen.getByTestId("development-container-runtime-status").textContent).toBe("daemon_changed");
		expect(screen.getByTestId("development-container-runtime-pinned-daemon").textContent).toBe("daemon-alpha");
		expect(screen.getByTestId("development-container-runtime-observed-daemon").textContent).toBe("daemon-beta");

		fireEvent.click(screen.getByTestId("development-container-runtime-confirm"));

		// The observed daemon, not the pinned one. Confirming the pinned id would approve the runtime that is already
		// approved and leave the operator stuck on a prompt that never clears.
		expect(onConfirm).toHaveBeenCalledWith("daemon-beta");
	});

	it("does not offer a confirmation for a permission failure, which no approval can fix", () => {
		renderPanel({
			ready: false,
			status: "permission_denied",
			message: "Development Mode found a container runtime but this node is not permitted to use it.",
			requiresOperatorConfirmation: false,
			endpoint: "unix:///var/run/docker.sock",
		});

		expect(screen.queryByTestId("development-container-runtime-confirm")).toBeNull();
	});

	it("surfaces a failed confirmation instead of silently doing nothing", () => {
		renderPanel(
			{
				ready: false,
				status: "daemon_changed",
				message: "pinned elsewhere",
				requiresOperatorConfirmation: true,
				observedDaemon: { daemonId: "daemon-beta", serverVersion: "28.0.0", endpoint: "unix:///x.sock" },
			},
			vi.fn(),
			"Could not confirm the container runtime.",
		);

		expect(screen.getByTestId("development-container-runtime-confirm-error").textContent).toBe(
			"Could not confirm the container runtime.",
		);
	});

	it("states that container-backed execution is not yet in use under the process provider", () => {
		// Phase 1 honesty check. Without this line an operator reading a green container-runtime banner would
		// reasonably conclude their Development Mode runs are already containerised. They are not.
		renderPanel({ ready: true, status: "ready", message: "ok", requiresOperatorConfirmation: false }, vi.fn(), undefined, "process");

		expect(screen.getByTestId("development-container-runtime-not-yet-in-use").textContent).toContain(
			"not switched on yet",
		);
	});

	it("stops claiming container execution is off once the container provider is the one in force", () => {
		// F-058: this sentence was hard-coded, so with `Development:Sandbox:Provider=docker` live and a container
		// demonstrably running it told the operator the opposite of what the same screen's banner said.
		renderPanel({ ready: true, status: "ready", message: "ok", requiresOperatorConfirmation: false }, vi.fn(), undefined, "docker");

		const note = screen.getByTestId("development-container-runtime-not-yet-in-use").textContent ?? "";
		expect(note).not.toContain("not switched on yet");
		expect(note).toContain("execute inside this container runtime");
	});

	it("renders nothing when the capability response carries no container runtime", () => {
		renderPanel(undefined);

		expect(screen.queryByTestId("development-container-runtime")).toBeNull();
	});
});
