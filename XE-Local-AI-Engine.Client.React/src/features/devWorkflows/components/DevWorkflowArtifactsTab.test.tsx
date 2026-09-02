// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { DevWorkflowArtifactsTab } from "@/features/devWorkflows/components/DevWorkflowArtifactsTab";
import { devWorkflowArtifact, devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";
import { renderWithProviders } from "@/test/RenderWithProviders";

const { run: runId, artifact: artifactId } = devWorkflowTestIds;

// Artifact ids are GUIDs on the wire and the generated client validates the path before it issues the request, so a
// readable stand-in like "v2" would fail validation rather than reach MSW.
const versionOneId = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa";
const versionTwoId = "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb";
const supersedingId = "cccccccc-3333-4333-8333-cccccccccccc";

function contentRoute(onRequest?: () => void) {
	return http.get(localApiPath(`development-workflows/runs/${runId}/artifacts/${artifactId}/content`), () => {
		onRequest?.();
		return HttpResponse.json({ artifact: devWorkflowArtifact(), content: "# Survey", isBase64: false });
	});
}

setupMswServer();

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

	it("collapses the versions of one lineage into a single row showing the latest", async () => {
		renderWithProviders(
			<DevWorkflowArtifactsTab
				runId={runId}
				artifacts={[
					devWorkflowArtifact({ id: versionOneId, version: 1, isLatest: false }),
					devWorkflowArtifact({ id: versionTwoId, version: 2, isLatest: true }),
				]}
			/>,
		);

		const row = await screen.findByTestId(`dev-workflow-artifact-${versionTwoId}`);
		expect(row.textContent).toContain("v2 · from research");
		expect(screen.queryByTestId(`dev-workflow-artifact-${versionOneId}`)).toBeNull();
	});

	it("re-keys the content request when the version picker moves to an older version", async () => {
		const requested: string[] = [];
		server.use(
			http.get(localApiPath(`development-workflows/runs/${runId}/artifacts/:artifactId/content`), ({ params }) => {
				requested.push(String(params["artifactId"]));
				return HttpResponse.json({ artifact: devWorkflowArtifact(), content: "# Survey", isBase64: false });
			}),
		);
		renderWithProviders(
			<DevWorkflowArtifactsTab
				runId={runId}
				artifacts={[
					devWorkflowArtifact({ id: versionOneId, version: 1, isLatest: false }),
					devWorkflowArtifact({ id: versionTwoId, version: 2, isLatest: true }),
				]}
			/>,
		);

		fireEvent.click(await screen.findByTestId(`dev-workflow-artifact-${versionTwoId}`));
		await waitFor(() => expect(requested).toContain(versionTwoId));

		// Mantine's Select is a combobox: open it, then pick the option by its rendered label.
		fireEvent.click(screen.getByTestId("dev-workflow-artifact-version"));
		fireEvent.click(await screen.findByText("v1"));

		await waitFor(() => expect(requested).toContain(versionOneId));
	});

	it("renders a version badge instead of a picker for a lineage with a single version", async () => {
		server.use(contentRoute());
		renderWithProviders(<DevWorkflowArtifactsTab runId={runId} artifacts={[devWorkflowArtifact()]} />);

		fireEvent.click(await screen.findByTestId(`dev-workflow-artifact-${artifactId}`));

		expect((await screen.findByTestId("dev-workflow-artifact-version-badge")).textContent).toBe("v1");
		expect(screen.queryByTestId("dev-workflow-artifact-version")).toBeNull();
	});

	it("links a stale artifact to the artifact that superseded it, selecting it in the same viewer", async () => {
		server.use(
			http.get(localApiPath(`development-workflows/runs/${runId}/artifacts/:artifactId/content`), () =>
				HttpResponse.json({ artifact: devWorkflowArtifact(), content: "# Survey", isBase64: false }),
			),
		);
		renderWithProviders(
			<DevWorkflowArtifactsTab
				runId={runId}
				artifacts={[
					devWorkflowArtifact({ id: artifactId, lineageId: "lineage-plan", isStale: true, staleBecauseArtifactId: supersedingId }),
					devWorkflowArtifact({ id: supersedingId, lineageId: "lineage-spec", name: "spec.md", version: 4 }),
				]}
			/>,
		);

		fireEvent.click(await screen.findByTestId(`dev-workflow-artifact-${artifactId}`));
		const link = await screen.findByTestId("dev-workflow-artifact-stale-because");
		expect(link.textContent).toContain("spec.md");

		fireEvent.click(link);

		await waitFor(() => expect(screen.getByTestId("dev-workflow-artifact-header").textContent).toContain("v4"));
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
