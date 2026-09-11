// @vitest-environment jsdom

// The trust action is the security-relevant one on this surface: it tells the node to accept a container runtime whose
// identity changed. It is gated on the SERVER's `requiresOperatorConfirmation`, never on the status the card happens
// to be rendering, and the id the operator is shown is the id that is sent — a daemon that changed again between
// render and click must be refused by the node, not silently trusted.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { RuntimeStatusCard } from "@/features/externalApps/components/RuntimeStatusCard";
import { type ContainerRuntimeStatus, containerRuntimeStatuses } from "@/features/externalApps/models/ExternalAppModels";
import { useExternalAppRuntime } from "@/features/externalApps/queries/useExternalApps";
import { externalAppRuntime, externalAppTestIds } from "@/features/externalApps/test/ExternalAppFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

function renderCard(node: ReactNode) {
	return renderWithProviders(<ConfirmProvider>{node}</ConfirmProvider>);
}

/** Records the body of every runtime-refresh the card sends. */
function captureRefresh(): { body: unknown }[] {
	const requests: { body: unknown }[] = [];
	server.use(
		http.post(localApiPath("external-apps/runtime/refresh"), async ({ request }) => {
			requests.push({ body: await request.json() });
			return HttpResponse.json(externalAppRuntime());
		}),
	);
	return requests;
}

// Keyed on the union, not on `string`: a status added to `containerRuntimeStatuses` fails to compile here instead of
// being looped over with no expectation of its own.
const statusLabels: Record<ContainerRuntimeStatus, readonly [string, string]> = {
	Ready: ["Ready", "The container runtime is running and XE can reach it."],
	DaemonUnreachable: ["Not reachable", "XE cannot reach the container runtime. Start Docker and check again."],
	PermissionDenied: ["No permission", "XE is not allowed to talk to the container runtime on this computer."],
	ApiVersionTooOld: ["Version too old", "The installed container runtime is too old for XE. Update Docker and check again."],
	DaemonIdentityChanged: [
		"Identity changed",
		"The container runtime on this computer is not the one XE trusted before. Confirm that this is expected.",
	],
	NotConfigured: ["Not set up", "No container runtime is set up on this computer. Install Docker to use External Apps."],
	ProbeFailed: ["Check failed", "The runtime check did not finish. Try again."],
};

/** The card as the catalog page mounts it: the `runtime` prop is the query's data, so an invalidation reaches it. */
function RuntimeCardFromQuery() {
	const runtimeQuery = useExternalAppRuntime();
	return <RuntimeStatusCard runtime={runtimeQuery.data} isLoading={runtimeQuery.isLoading} />;
}

describe("RuntimeStatusCard", () => {
	it("renders a label and a what-to-do hint for every one of the seven statuses", () => {
		for (const status of containerRuntimeStatuses) {
			const [label, hint] = statusLabels[status];
			const { unmount } = renderCard(<RuntimeStatusCard runtime={externalAppRuntime({ status })} isLoading={false} />);

			expect(screen.getByTestId("external-app-runtime-status").textContent).toContain(label);
			expect(screen.getByText(hint)).toBeDefined();
			unmount();
		}
	});

	it("warns for every status but Ready", () => {
		const { unmount } = renderCard(<RuntimeStatusCard runtime={externalAppRuntime()} isLoading={false} />);
		expect(screen.queryByTestId("external-app-runtime-alert")).toBeNull();
		unmount();

		renderCard(<RuntimeStatusCard runtime={externalAppRuntime({ status: "DaemonUnreachable" })} isLoading={false} />);
		expect(screen.getByTestId("external-app-runtime-alert")).toBeDefined();
	});

	it("appends the server's own message after the local hint", () => {
		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({ status: "ProbeFailed", message: "The probe timed out after 5 seconds." })}
				isLoading={false}
			/>,
		);

		expect(screen.getByText("The probe timed out after 5 seconds.")).toBeDefined();
	});

	it("names the features a partly-capable runtime does not offer", () => {
		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({
					status: "ApiVersionTooOld",
					capabilities: {
						containers: true,
						networks: true,
						bindStorage: true,
						loopbackPortPublishing: true,
						healthChecks: false,
						restartPolicies: true,
						logs: true,
						imagePull: true,
						gpuDevices: false,
					},
				})}
				isLoading={false}
			/>,
		);

		expect(screen.getByTestId("external-app-runtime-missing-capabilities").textContent).toContain(
			"This computer is missing: checking whether an application is healthy, passing the graphics card to an application",
		);
	});

	// The node zeroes the whole capability bag whenever the runtime is not ready, so a stopped daemon would otherwise
	// report nine separate missing features instead of the one sentence that says to start Docker.
	it("says nothing about features when the runtime granted none at all", () => {
		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({
					status: "DaemonUnreachable",
					capabilities: {
						containers: false,
						networks: false,
						bindStorage: false,
						loopbackPortPublishing: false,
						healthChecks: false,
						restartPolicies: false,
						logs: false,
						imagePull: false,
						gpuDevices: false,
					},
				})}
				isLoading={false}
			/>,
		);

		expect(screen.queryByTestId("external-app-runtime-missing-capabilities")).toBeNull();
	});

	// R2-26: the node NEVER removes a container carrying another install id, so the count has to be visible or an
	// operator reads a clean Installed list as "XE owns every labelled container on this machine".
	it("reports containers left behind by another XE installation", () => {
		renderCard(<RuntimeStatusCard runtime={externalAppRuntime({ foreignInstallContainers: 2 })} isLoading={false} />);

		expect(screen.getByTestId("external-app-runtime-foreign-containers").textContent).toContain(
			"XE found 2 containers from another XE installation",
		);
	});

	// The real path: `GET external-apps/runtime` reports 0 on purpose (a pure read must not republish the reconciler's
	// last observation as a fresh one), so the count reaches the card only through the refresh RESPONSE.
	it("reports the count the refresh observed, not the one the pure read carries", async () => {
		server.use(
			http.post(localApiPath("external-apps/runtime/refresh"), () =>
				HttpResponse.json(externalAppRuntime({ foreignInstallContainers: 1 })),
			),
		);
		renderCard(<RuntimeStatusCard runtime={externalAppRuntime({ foreignInstallContainers: 0 })} isLoading={false} />);

		expect(screen.queryByTestId("external-app-runtime-foreign-containers")).toBeNull();

		fireEvent.click(screen.getByTestId("external-app-runtime-refresh"));

		await waitFor(() =>
			expect(screen.getByTestId("external-app-runtime-foreign-containers").textContent).toContain(
				"XE found 1 container from another XE installation",
			),
		);
	});

	it("says nothing about other installations when there are none", () => {
		renderCard(<RuntimeStatusCard runtime={externalAppRuntime({ foreignInstallContainers: 0 })} isLoading={false} />);

		expect(screen.queryByTestId("external-app-runtime-foreign-containers")).toBeNull();
	});

	it("says the runtime is unreachable without repainting the state below it", () => {
		renderCard(<RuntimeStatusCard runtime={externalAppRuntime({ available: false })} isLoading={false} />);

		expect(screen.getByTestId("external-app-runtime-unavailable")).toBeDefined();
		expect(screen.getByTestId("external-app-runtime-status").textContent).toContain("Ready");
	});

	it("re-probes without approving anything when Check again is used", async () => {
		const requests = captureRefresh();
		renderCard(<RuntimeStatusCard runtime={externalAppRuntime()} isLoading={false} />);

		fireEvent.click(screen.getByTestId("external-app-runtime-refresh"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({});
	});

	it("offers the trust action only when the server asks for a decision", () => {
		const { unmount } = renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({ status: "DaemonIdentityChanged", requiresOperatorConfirmation: false })}
				isLoading={false}
			/>,
		);
		// The status alone must not offer it: the gate is the server's flag, not a literal the client re-derives.
		expect(screen.queryByTestId("external-app-runtime-trust")).toBeNull();
		unmount();

		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({ status: "DaemonIdentityChanged", requiresOperatorConfirmation: true })}
				isLoading={false}
			/>,
		);
		expect(screen.getByTestId("external-app-runtime-trust")).toBeDefined();
	});

	it("offers no trust action when there is no observed daemon to approve", () => {
		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({
					status: "DaemonIdentityChanged",
					requiresOperatorConfirmation: true,
					observedDaemon: null,
				})}
				isLoading={false}
			/>,
		);

		expect(screen.queryByTestId("external-app-runtime-trust")).toBeNull();
	});

	it("shows the observed daemon id in the confirmation and sends that same id", async () => {
		const requests = captureRefresh();
		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({ status: "DaemonIdentityChanged", requiresOperatorConfirmation: true })}
				isLoading={false}
			/>,
		);

		fireEvent.click(screen.getByTestId("external-app-runtime-trust"));
		expect(await screen.findByText(new RegExp(externalAppTestIds.daemon))).toBeDefined();
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({ acknowledgeDaemonId: externalAppTestIds.daemon });
	});

	it("sends nothing when the operator declines the confirmation", async () => {
		const requests = captureRefresh();
		renderCard(
			<RuntimeStatusCard
				runtime={externalAppRuntime({ status: "DaemonIdentityChanged", requiresOperatorConfirmation: true })}
				isLoading={false}
			/>,
		);

		fireEvent.click(screen.getByTestId("external-app-runtime-trust"));
		fireEvent.click(await screen.findByTestId("confirm-cancel"));

		expect(requests).toHaveLength(0);
	});

	// The node answers 400 when the daemon changed AGAIN between the render and the click. Toasting alone left the
	// card holding the id it had just been refused for, so every further click sent the same rejected id.
	it("re-reads the runtime after a refused trust request, so the card offers the daemon that is there now", async () => {
		const secondDaemon = "ZZZZ:ZZZZ:ZZZZ:ZZZZ";
		const sent: unknown[] = [];
		let runtimeReads = 0;
		server.use(
			http.get(localApiPath("external-apps/runtime"), () => {
				runtimeReads += 1;
				return HttpResponse.json(
					externalAppRuntime({
						status: "DaemonIdentityChanged",
						requiresOperatorConfirmation: true,
						observedDaemon: {
							daemonId: runtimeReads === 1 ? externalAppTestIds.daemon : secondDaemon,
							serverVersion: "27.1.1",
							endpoint: "unix:///var/run/docker.sock",
							confirmedAtUtc: 1_700_000_000_000,
						},
					}),
				);
			}),
			http.post(localApiPath("external-apps/runtime/refresh"), async ({ request }) => {
				sent.push(await request.json());
				return HttpResponse.json(
					{ type: "about:blank", title: "Bad Request", status: 400, detail: "The daemon changed again." },
					{ status: 400 },
				);
			}),
		);
		renderCard(<RuntimeCardFromQuery />);

		fireEvent.click(await screen.findByTestId("external-app-runtime-trust"));
		expect(await screen.findByText(new RegExp(externalAppTestIds.daemon))).toBeDefined();
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(sent).toHaveLength(1));

		// The refusal is itself news about the daemon: the card re-reads and now offers the identity that is there.
		await waitFor(() => expect(runtimeReads).toBe(2));
		fireEvent.click(screen.getByTestId("external-app-runtime-trust"));
		expect(await screen.findByText(new RegExp(secondDaemon))).toBeDefined();
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(sent).toHaveLength(2));
		expect(sent[1]).toEqual({ acknowledgeDaemonId: secondDaemon });
	});

	it("renders a placeholder while the runtime is still being read", () => {
		renderCard(<RuntimeStatusCard runtime={undefined} isLoading={true} />);

		expect(screen.getByTestId("external-app-runtime-card")).toBeDefined();
		expect(screen.queryByTestId("external-app-runtime-refresh")).toBeNull();
	});
});
