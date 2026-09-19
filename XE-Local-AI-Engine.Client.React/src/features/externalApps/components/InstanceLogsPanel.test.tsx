// @vitest-environment jsdom

// `truncated` is the assertion that matters: a tail cut at the bound and a complete log look identical without the
// line above it, and an operator reading the second as the first concludes the application printed nothing else.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { InstanceLogsPanel } from "@/features/externalApps/components/InstanceLogsPanel";
import {
	externalAppInstance,
	externalAppInstanceLogs,
	externalAppManifest,
	externalAppTestIds,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const instanceId = externalAppTestIds.instance;
const logsPath = `external-apps/instances/${instanceId}/logs`;

function service(name: string) {
	return {
		name,
		image: "example/image",
		imageTag: "1",
		entrypoint: null,
		command: null,
		environment: {},
		ports: [],
		storage: [],
		hasHealthcheck: false,
		dependsOn: [],
		capAdd: [],
		extraHosts: [],
		readOnlyRootFilesystem: false,
	};
}

describe("InstanceLogsPanel", () => {
	it("renders the log text the server returned", async () => {
		server.use(jsonRoute("get", logsPath, externalAppInstanceLogs({ text: "listening on :80\n" })));
		renderWithProviders(<InstanceLogsPanel instance={externalAppInstance()} />);

		await waitFor(() => expect(screen.getByTestId("external-app-logs-text").textContent).toContain("listening on :80"));
		expect(screen.queryByTestId("external-app-logs-truncated")).toBeNull();
	});

	it("names the truncation and its line count when the tail was cut at the bound", async () => {
		server.use(jsonRoute("get", logsPath, externalAppInstanceLogs({ truncated: true, lineCount: 2000 })));
		renderWithProviders(<InstanceLogsPanel instance={externalAppInstance()} />);

		await waitFor(() => expect(screen.getByTestId("external-app-logs-truncated")).toBeDefined());
		expect(screen.getByTestId("external-app-logs-truncated").textContent).toContain("2000");
	});

	it("re-reads with the chosen bound when the tail size changes", async () => {
		const tails: (string | null)[] = [];
		server.use(
			http.get(localApiPath(logsPath), ({ request }) => {
				tails.push(new URL(request.url).searchParams.get("tail"));
				return HttpResponse.json(externalAppInstanceLogs());
			}),
		);
		renderWithProviders(<InstanceLogsPanel instance={externalAppInstance()} />);
		await waitFor(() => expect(tails).toEqual(["500"]));

		fireEvent.click(screen.getByText("2000"));

		// The server refuses a tail above its cap rather than clamping it, so 2000 is the largest choice offered.
		await waitFor(() => expect(tails).toEqual(["500", "2000"]));
	});

	it("hides the service picker for a single-part application and shows it for two", async () => {
		server.use(jsonRoute("get", logsPath, externalAppInstanceLogs()));
		const { unmount } = renderWithProviders(
			<InstanceLogsPanel instance={externalAppInstance({ manifest: externalAppManifest({ services: [service("web")] }) })} />,
		);
		await waitFor(() => expect(screen.getByTestId("external-app-logs-text")).toBeDefined());
		expect(screen.queryByTestId("external-app-logs-service")).toBeNull();
		unmount();

		renderWithProviders(
			<InstanceLogsPanel
				instance={externalAppInstance({ manifest: externalAppManifest({ services: [service("web"), service("worker")] }) })}
			/>,
		);
		await waitFor(() => expect(screen.getByTestId("external-app-logs-service")).toBeDefined());
	});

	it("renders the empty state when the part printed nothing", async () => {
		server.use(jsonRoute("get", logsPath, externalAppInstanceLogs({ text: "" })));
		renderWithProviders(<InstanceLogsPanel instance={externalAppInstance()} />);

		await waitFor(() => expect(screen.getByTestId("external-app-logs-empty")).toBeDefined());
		expect(screen.queryByTestId("external-app-logs-error")).toBeNull();
	});

	// "No log output" is a claim about the CONTAINER. Standing in for a 503 it told the operator the application had
	// printed nothing when the truth was that the runtime could not be reached at all.
	it("says the read failed instead of claiming the part printed nothing", async () => {
		server.use(problemDetailsRoute("get", logsPath, 503, { detail: "The container runtime is not available." }));
		renderWithProviders(<InstanceLogsPanel instance={externalAppInstance()} />);

		await waitFor(() => expect(screen.getByTestId("external-app-logs-error")).toBeDefined());
		expect(screen.getByTestId("external-app-logs-error").textContent).toContain("The container runtime is not available.");
		expect(screen.queryByTestId("external-app-logs-empty")).toBeNull();
	});

	// A failed REFRESH keeps the last good tail on screen, which is the useful thing to keep — but silently, an
	// operator reads a stale tail as current output.
	it("keeps the last good output on screen and says the refresh failed", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(logsPath), () => {
				reads += 1;
				return reads === 1
					? HttpResponse.json(externalAppInstanceLogs({ text: "listening on :80\n" }))
					: HttpResponse.json(
							{ type: "about:blank", title: "Service Unavailable", status: 503, detail: "The runtime went away." },
							{ status: 503, headers: { "content-type": "application/problem+json" } },
						);
			}),
		);
		renderWithProviders(<InstanceLogsPanel instance={externalAppInstance()} />);
		await waitFor(() => expect(screen.getByTestId("external-app-logs-text")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-logs-refresh"));

		await waitFor(() => expect(screen.getByTestId("external-app-logs-error").textContent).toContain("The runtime went away."));
		expect(screen.getByTestId("external-app-logs-text").textContent).toContain("listening on :80");
	});
});
