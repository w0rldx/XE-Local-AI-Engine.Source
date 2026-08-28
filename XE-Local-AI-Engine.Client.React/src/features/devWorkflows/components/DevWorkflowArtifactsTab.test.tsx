// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { DevWorkflowArtifactsTab } from "@/features/devWorkflows/components/DevWorkflowArtifactsTab";
import { devWorkflowArtifact, devWorkflowTestIds } from "@/features/devWorkflows/models/DevWorkflowTestFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";

const { run: runId, artifact: artifactId } = devWorkflowTestIds;

function contentRoute(onRequest?: () => void) {
	return http.get(localApiPath(`development-workflows/runs/${runId}/artifacts/${artifactId}/content`), () => {
		onRequest?.();
		return HttpResponse.json({ artifact: devWorkflowArtifact(), content: "# Survey", isBase64: false });
	});
}

describe("DevWorkflowArtifactsTab", () => {
	afterEach(() => {
		cleanup();
	});

	it("lists an artifact with its kind, version and producing node", async () => {
		renderWithProviders(<DevWorkflowArtifactsTab runId={runId} artifacts={[devWorkflowArtifact()]} />);

		const row = await screen.findByTestId(`dev-workflow-artifact-${artifactId}`);
		expect(row.textContent).toContain("vector-store-survey.md");
		expect(row.textContent).toContain("Research");
		expect(row.textContent).toContain("v1 · from research");
	});

	it("prints an unrecognised kind's own token rather than relabelling it as a known one", () => {
		renderWithProviders(<DevWorkflowArtifactsTab runId={runId} artifacts={[devWorkflowArtifact({ kind: "Blueprint" })]} />);

		expect(screen.getByTestId(`dev-workflow-artifact-${artifactId}`).textContent).toContain("Blueprint");
	});

	it("badges a superseded artifact as stale — mark-only, with no regenerate control to imply otherwise", () => {
		renderWithProviders(
			<DevWorkflowArtifactsTab
				runId={runId}
				artifacts={[devWorkflowArtifact({ isStale: true, staleBecauseArtifactId: "other" })]}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-artifact-stale-${artifactId}`).textContent).toBe("Stale");
	});

	it("loads the content into the read-only editor when a row is picked", async () => {
		server.use(contentRoute());
		renderWithProviders(<DevWorkflowArtifactsTab runId={runId} artifacts={[devWorkflowArtifact()]} />);

		fireEvent.click(await screen.findByTestId(`dev-workflow-artifact-${artifactId}`));

		await waitFor(() => expect(screen.getByTestId("dev-workflow-artifact-viewer")).toBeDefined());
	});

	it("issues NO content request for an artifact whose blob is unreadable", async () => {
		let requested = false;
		server.use(contentRoute(() => {
			requested = true;
		}));
		renderWithProviders(<DevWorkflowArtifactsTab runId={runId} artifacts={[devWorkflowArtifact({ isValid: false })]} />);

		fireEvent.click(await screen.findByTestId(`dev-workflow-artifact-${artifactId}`));

		expect(await screen.findByTestId("dev-workflow-artifact-invalid")).toBeDefined();
		expect(requested).toBe(false);
	});

	it("renders an empty state rather than an empty list", () => {
		renderWithProviders(<DevWorkflowArtifactsTab runId={runId} artifacts={[]} />);

		expect(screen.getByTestId("dev-workflow-artifacts-empty")).toBeDefined();
	});
});
