// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AgentHomePatchApplyDialog } from "@/core/ui/components/AgentHomePatchApplyDialog/AgentHomePatchApplyDialog";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Driven through the real generated SDK and the shared axios interceptors against MSW, so the wire shape and the
// ProblemDetails → ApiError path are exercised rather than stubbed. What this file owes is the ORDER of the flow: the
// dialog must read before it offers, and it must send back the hash it was shown.
const server = setupMswServer();

const RUN_ID = "run-1758300000000-1";
const PATCH_HASH = "a".repeat(64);

const PREVIEW_ROUTE = `agent-home/runs/${RUN_ID}/patch/preview`;
const APPLY_ROUTE = `agent-home/runs/${RUN_ID}/patch/apply`;

const cleanPreview = {
	canApply: true,
	files: [{ alias: "repo-01", relativePath: "src/App.cs", changeType: "modified", added: 3, removed: 1 }],
	rejections: [],
	dirtyTargets: [],
	dirtyCheckUnavailable: false,
	containsBinary: false,
	patchSha256: PATCH_HASH,
};

/** The node's 409 shape: the service's own redacted reasons, one per `errors` entry. */
function conflict(reasons: readonly string[], extra: Record<string, unknown> = {}) {
	return HttpResponse.json(
		{
			type: "about:blank",
			title: "Conflict",
			status: 409,
			detail: "One or more errors occurred.",
			errors: reasons.map((reason) => ({ name: "generalErrors", reason })),
			...extra,
		},
		{ status: 409, headers: { "content-type": "application/problem+json" } },
	);
}

function renderDialog(onClose = vi.fn()) {
	const result = renderWithProviders(<AgentHomePatchApplyDialog runId={RUN_ID} onClose={onClose} />);
	return { ...result, onClose };
}

describe("AgentHomePatchApplyDialog", () => {
	afterEach(cleanup);

	it("reads the plan on open and lists each file with its folder, change type and line counts", async () => {
		server.use(jsonRoute("post", PREVIEW_ROUTE, cleanPreview));
		renderDialog();

		const table = await screen.findByTestId("agent-home-patch-apply-files");
		expect(table.textContent).toContain("repo-01");
		expect(table.textContent).toContain("src/App.cs");
		expect(table.textContent).toContain("modified");
		expect(table.textContent).toContain("+3 −1");
	});

	it("sends the hash the preview reported, and reports what landed", async () => {
		const bodies: unknown[] = [];
		server.use(
			jsonRoute("post", PREVIEW_ROUTE, cleanPreview),
			http.post(localApiPath(APPLY_ROUTE), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json({ appliedFiles: cleanPreview.files });
			}),
		);
		renderDialog();

		const confirm = await screen.findByTestId("agent-home-patch-apply-confirm");
		await waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(confirm);

		const applied = await screen.findByTestId("agent-home-patch-apply-applied");
		expect(applied.textContent).toContain("repo-01/src/App.cs");
		// The binding, at the wire: an apply that did not carry the previewed hash could land a patch the operator
		// never read, and the node would have no way to tell.
		expect(bodies).toEqual([{ patchSha256: PATCH_HASH }]);
		// The apply is gone once it succeeded, so the same approval cannot be replayed from the open dialog.
		expect(screen.queryByTestId("agent-home-patch-apply-confirm")).toBeNull();
	});

	it("keeps Apply disabled and shows the reasons when the patch cannot be applied", async () => {
		server.use(
			jsonRoute("post", PREVIEW_ROUTE, {
				...cleanPreview,
				canApply: false,
				rejections: [{ reason: "alias 'repo-01': patch does not apply cleanly (error: patch failed)", path: null }],
			}),
		);
		renderDialog();

		const rejections = await screen.findByTestId("agent-home-patch-apply-rejections");
		expect(rejections.textContent).toContain("does not apply cleanly");
		expect((screen.getByTestId("agent-home-patch-apply-confirm") as HTMLButtonElement).disabled).toBe(true);
	});

	it("names the file each refusal is about, and keeps patch-wide reasons separate", async () => {
		server.use(
			jsonRoute("post", PREVIEW_ROUTE, {
				...cleanPreview,
				canApply: false,
				rejections: [
					{ reason: "a patch block creates or changes a symbolic link, which is not supported.", path: "repo-01/evil" },
					{ reason: "the exported patch contains no file changes.", path: null },
				],
			}),
		);
		renderDialog();

		const rejections = await screen.findByTestId("agent-home-patch-apply-rejections");
		expect(rejections.textContent).toContain("repo-01/evil");
		expect(rejections.textContent).toContain("symbolic link");
		expect(rejections.textContent).toContain("contains no file changes");
	});

	// Advisory, never a gate: the node says the patch applies, so Apply stays available and the operator decides.
	it("warns about targets that already carry local changes without disabling Apply", async () => {
		server.use(
			jsonRoute("post", PREVIEW_ROUTE, {
				...cleanPreview,
				dirtyTargets: [
					{ path: "repo-01/src/App.cs", state: "modified" },
					{ path: "repo-01/notes/new.txt", state: "untracked" },
				],
			}),
		);
		renderDialog();

		const warning = await screen.findByTestId("agent-home-patch-apply-dirty");
		expect(warning.textContent).toContain("repo-01/src/App.cs");
		expect(warning.textContent).toContain("repo-01/notes/new.txt");
		// Announced rather than silently painted, and a real list for a list of files.
		expect(warning.getAttribute("role")).toBe("status");
		expect(warning.querySelectorAll("li")).toHaveLength(2);

		const confirm = await screen.findByTestId("agent-home-patch-apply-confirm");
		await waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
	});

	it("says so when a folder's local state could not be read", async () => {
		server.use(jsonRoute("post", PREVIEW_ROUTE, { ...cleanPreview, dirtyCheckUnavailable: true }));
		renderDialog();

		const warning = await screen.findByTestId("agent-home-patch-apply-dirty");
		expect(warning.textContent).toContain("could not be read");
	});

	it("shows no dirty-target warning when nothing on the host has drifted", async () => {
		server.use(jsonRoute("post", PREVIEW_ROUTE, cleanPreview));
		renderDialog();

		await screen.findByTestId("agent-home-patch-apply-files");
		expect(screen.queryByTestId("agent-home-patch-apply-dirty")).toBeNull();
	});

	it("warns about binary changes the operator cannot review here", async () => {
		server.use(jsonRoute("post", PREVIEW_ROUTE, { ...cleanPreview, containsBinary: true }));
		renderDialog();

		expect(await screen.findByTestId("agent-home-patch-apply-binary")).toBeTruthy();
	});

	// The whole point of the hash: the file changed under the operator, so the approval no longer describes anything.
	// The dialog stays open with the reason and a way to read the new state.
	it("stays open on a hash-mismatch 409 and re-reads the plan when asked", async () => {
		let previews = 0;
		server.use(
			http.post(localApiPath(PREVIEW_ROUTE), () => {
				previews += 1;
				return HttpResponse.json(cleanPreview);
			}),
			http.post(localApiPath(APPLY_ROUTE), () => conflict(["the exported patch changed since it was previewed."])),
		);
		const { onClose } = renderDialog();

		const confirm = await screen.findByTestId("agent-home-patch-apply-confirm");
		await waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(confirm);

		const failure = await screen.findByTestId("agent-home-patch-apply-error");
		expect(failure.textContent).toContain("changed since it was previewed");
		expect(onClose).not.toHaveBeenCalled();
		expect(await waitFor(() => previews)).toBe(1);

		fireEvent.click(screen.getByTestId("agent-home-patch-apply-repreview"));

		await waitFor(() => expect(previews).toBe(2));
		await waitFor(() => expect(screen.queryByTestId("agent-home-patch-apply-error")).toBeNull());
	});

	// The apply's 409 carries the refused entry in the error NAME, which is what lets the dialog group it per file.
	it("names the refused file on a 409 that carries one", async () => {
		server.use(
			jsonRoute("post", PREVIEW_ROUTE, cleanPreview),
			http.post(localApiPath(APPLY_ROUTE), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "One or more errors occurred.",
						errors: [
							{ name: "repo-01/evil", reason: "a patch block creates or changes a symbolic link, which is not supported." },
						],
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderDialog();

		const confirm = await screen.findByTestId("agent-home-patch-apply-confirm");
		await waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(confirm);

		const failure = await screen.findByTestId("agent-home-patch-apply-error");
		expect(failure.textContent).toContain("repo-01/evil");
		expect(failure.textContent).toContain("symbolic link");
	});

	it("shows a refused apply's reasons without closing", async () => {
		server.use(
			jsonRoute("post", PREVIEW_ROUTE, cleanPreview),
			http.post(localApiPath(APPLY_ROUTE), () => conflict(["alias 'repo-01': a target path escapes the folder root."])),
		);
		const { onClose } = renderDialog();

		const confirm = await screen.findByTestId("agent-home-patch-apply-confirm");
		await waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(confirm);

		const failure = await screen.findByTestId("agent-home-patch-apply-error");
		expect(failure.textContent).toContain("escapes the folder root");
		expect(onClose).not.toHaveBeenCalled();
	});

	it("reports a run whose patch is gone as a read failure rather than an empty plan", async () => {
		server.use(
			http.post(localApiPath(PREVIEW_ROUTE), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Not Found", status: 404, detail: "" },
					{ status: 404, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderDialog();

		expect(await screen.findByTestId("agent-home-patch-apply-preview-error")).toBeTruthy();
		expect(screen.queryByTestId("agent-home-patch-apply-confirm")?.hasAttribute("disabled")).toBe(true);
	});
});
