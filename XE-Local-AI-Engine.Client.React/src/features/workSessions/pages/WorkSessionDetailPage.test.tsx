// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ReactNode } from "react";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import type { ChatScope } from "@/features/chat/models/ChatModels";
import { WorkSessionDetailPage } from "@/features/workSessions/pages/WorkSessionDetailPage";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer(capabilityRoute());

// The centre pane IS the real chat page; its own contract is pinned by Chat.scope.test.tsx. What this file needs is
// the SCOPE the detail page hands it and a way to fire the two overrides.
const lastScope = vi.hoisted(() => ({ current: undefined as ChatScope | undefined }));

vi.mock("@/features/chat/pages/Chat", () => ({
	Chat: ({ scope }: { scope?: ChatScope }) => {
		lastScope.current = scope;
		return (
			<div
				data-testid="embedded-chat"
				data-conversation={scope?.conversationId}
				data-agent={scope?.pinnedAgentId}
				data-composer-disabled={String(scope?.composerDisabled === true)}
				data-embedded={String(scope?.embedded === true)}
				data-resume-nonce={String(scope?.resumeNonce ?? 0)}
			>
				<button
					type="button"
					data-testid="embedded-send"
					onClick={() => {
						scope?.onSendOverride?.("check the second source").catch(() => undefined);
					}}
				>
					send
				</button>
				<button type="button" data-testid="embedded-stop" onClick={() => scope?.onStopOverride?.()}>
					stop
				</button>
			</div>
		);
	},
}));

const navigateSpy = vi.hoisted(() => vi.fn());

// The app router is built from routeTree.gen.ts; a unit test only needs the navigate CALL, not a real route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigateSpy,
}));

const hubMock = vi.hoisted(() => {
	const connection = { state: "Connected", on: vi.fn(), off: vi.fn(), invoke: vi.fn() };
	return {
		connection,
		acquire: vi.fn(() => ({
			connection,
			whenStarted: Promise.resolve(),
			onReconnected: vi.fn(() => vi.fn()),
			release: vi.fn(),
		})),
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: hubMock.acquire }));

const sessionId = "aaaaaaaa-0000-4000-8000-000000000001";
const conversationId = "bbbbbbbb-0000-4000-8000-000000000002";
const agentId = "cccccccc-0000-4000-8000-000000000003";

function session(overrides: Record<string, unknown> = {}) {
	return {
		id: sessionId,
		title: "Survey the vector-store options",
		objective: "Compare the options",
		kind: "Research",
		agentDefinitionId: agentId,
		conversationId,
		status: "Running",
		currentTaskId: null,
		stepCount: 3,
		maxStepsPerRun: 25,
		lastCheckpointId: null,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		version: 1,
		lastSequence: 5,
		...overrides,
	};
}

/**
 * The node's WorkSessions:Enabled switch, which this page reads before anything else. It ships ON, but an operator
 * can turn it off — and this route is still reachable by bookmark when they have.
 */
function capabilityRoute(enabled = true) {
	return jsonRoute("get", "work-sessions/capability", { enabled });
}

/** The feeds the page mounts alongside the session detail. Each is read once even when the detail itself 404s. */
function subordinateFeedRoutes() {
	return [
		jsonRoute("get", `work-sessions/${sessionId}/tasks`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/findings`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/artifacts`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/checkpoints`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/events`, { items: [], lastSequence: 0, hasMore: false }),
	];
}

function routes(sessionBody: Record<string, unknown> = session()) {
	server.use(jsonRoute("get", `work-sessions/${sessionId}`, sessionBody), ...subordinateFeedRoutes());
}

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
}

/**
 * jsdom gives every element a `clientWidth` of 0, and `usePaneLayoutMode` measures in a layout effect — before a test
 * could get a handle on the node — so the stub goes on the prototype. Removed again in `afterEach`.
 */
function stubContainerWidth(width: number): void {
	Object.defineProperty(HTMLElement.prototype, "clientWidth", { configurable: true, get: () => width });
}

// The page's delete action routes through the shared confirm dialog, which throws without a provider.
const confirmResult = { value: true };
const confirmSpy = vi.fn(() => Promise.resolve(confirmResult.value));

function renderDetail(node: ReactNode, options: { withRouter?: boolean } = {}) {
	return renderWithProviders(<ConfirmContext.Provider value={{ confirm: confirmSpy }}>{node}</ConfirmContext.Provider>, options);
}

describe("WorkSessionDetailPage", () => {
	beforeEach(() => {
		// jsdom's default; the desktop layout is the default under test, matching ChatDisplayShell's own reasoning.
		setViewportWidth(1024);
		confirmResult.value = true;
		hubMock.connection.invoke.mockResolvedValue({
			sessionId,
			status: "Running",
			step: 3,
			currentTaskId: null,
			lastSeq: 5,
			events: [],
			replayTruncated: false,
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
		// A no-op when the test never stubbed it; jsdom's own prototype getter comes back either way.
		Reflect.deleteProperty(HTMLElement.prototype, "clientWidth");
	});

	it("renders the three panes side by side at desktop width", async () => {
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		const grid = await screen.findByTestId("work-session-detail-grid");
		// The centre track carries a floor, like the dev-workflow run page it shares this template with: without one
		// a viewport just above 1024 squeezed it under its own chrome, and FullHeightPage clips the X axis, so the
		// grid needs its own horizontal scroller for the overflow the floor makes honest.
		expect(grid.style.gridTemplateColumns).toBe("320px minmax(240px, 1fr) minmax(380px, 420px)");
		expect(grid.style.overflowX).toBe("auto");
		expect(screen.getByTestId("work-session-plan-panel")).toBeDefined();
		expect(screen.getByTestId("work-session-conversation-pane")).toBeDefined();
		expect(screen.getByTestId("work-session-side-panel")).toBeDefined();
		// No drawer toggles on desktop — the panes are already on screen.
		expect(screen.queryByTestId("work-session-plan-toggle")).toBeNull();
		expect(screen.queryByTestId("work-session-side-toggle")).toBeNull();
	});

	it("collapses to the conversation with two drawers below 1024px", async () => {
		setViewportWidth(800);
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		await screen.findByTestId("work-session-conversation-pane");
		expect(screen.queryByTestId("work-session-detail-grid")).toBeNull();
		expect(screen.queryByTestId("work-session-plan-panel")).toBeNull();
		expect(screen.queryByTestId("work-session-side-panel")).toBeNull();

		fireEvent.click(screen.getByTestId("work-session-plan-toggle"));
		expect(await screen.findByTestId("work-session-plan-panel")).toBeDefined();

		fireEvent.click(screen.getByTestId("work-session-side-toggle"));
		expect(await screen.findByTestId("work-session-side-panel")).toBeDefined();
	});

	// The grid lives inside the app shell, so a wide WINDOW is not a wide container: the sidebar and the content
	// padding take ~250px before the panes see any of it, and the sidebar collapses with no resize event at all.
	// One decision drives both halves, so the header can no longer withhold the toggles while the panes are stacked.
	it("collapses on a wide viewport when the container is too narrow for three columns", async () => {
		setViewportWidth(1600);
		stubContainerWidth(725);
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		await screen.findByTestId("work-session-conversation-pane");
		expect(screen.queryByTestId("work-session-detail-grid")).toBeNull();
		expect(screen.getByTestId("work-session-plan-toggle")).toBeDefined();
		expect(screen.getByTestId("work-session-side-toggle")).toBeDefined();
	});

	it("pins the owned conversation and the session's agent into the embedded chat", async () => {
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		const chat = await screen.findByTestId("embedded-chat");
		expect(chat.getAttribute("data-conversation")).toBe(conversationId);
		expect(chat.getAttribute("data-agent")).toBe(agentId);
		expect(chat.getAttribute("data-embedded")).toBe("true");
		expect(chat.getAttribute("data-composer-disabled")).toBe("false");
	});

	it("disables the composer once the session is terminal", async () => {
		routes(session({ status: "Completed" }));
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		const chat = await screen.findByTestId("embedded-chat");
		expect(chat.getAttribute("data-composer-disabled")).toBe("true");
	});

	it("posts a follow-up through the work-session route, never a chat invocation", async () => {
		routes();
		const posted: string[] = [];
		server.use(
			http.post(localApiPath(`work-sessions/${sessionId}/messages`), async ({ request }) => {
				const body = (await request.json()) as { text: string };
				posted.push(body.text);
				return HttpResponse.json({ messageId: "dddddddd-0000-4000-8000-000000000004", conversationId }, { status: 202 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("embedded-send"));

		await waitFor(() => expect(posted).toEqual(["check the second source"]));
	});

	it("surfaces a rejected follow-up inline AND rejects the override, which is what keeps the draft", async () => {
		routes();
		server.use(
			problemDetailsRoute("post", `work-sessions/${sessionId}/messages`, 400, {
				detail: "Message exceeds the node's size limit.",
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);
		await screen.findByTestId("embedded-chat");

		// Driven directly rather than through the button, because the REJECTION is the contract: ChatInputArea only
		// keeps the draft when the returned promise rejects (see Chat.scope.test.tsx).
		await expect(lastScope.current?.onSendOverride?.("an oversized follow-up")).rejects.toThrow();

		const inlineError = await screen.findByTestId("work-session-follow-up-error");
		expect(inlineError.textContent).toContain("size limit");
	});

	it("maps the chat stop button onto pause", async () => {
		routes();
		let paused = 0;
		server.use(
			http.post(localApiPath(`work-sessions/${sessionId}/pause`), () => {
				paused += 1;
				return HttpResponse.json(session({ status: "Paused" }));
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("embedded-stop"));

		await waitFor(() => expect(paused).toBe(1));
	});

	it("sends the lifecycle command the status offers", async () => {
		routes(session({ status: "Draft" }));
		let started = 0;
		server.use(
			http.post(localApiPath(`work-sessions/${sessionId}/start`), () => {
				started += 1;
				return HttpResponse.json(session({ status: "Running" }), { status: 202 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-start"));

		await waitFor(() => expect(started).toBe(1));
	});

	it("saves a title and objective edit, carrying the unchanged agent through the PATCH", async () => {
		routes(session({ status: "Paused" }));
		const patched: Array<{ title: string; objective: string; agentDefinitionId: string }> = [];
		server.use(
			http.patch(localApiPath(`work-sessions/${sessionId}`), async ({ request }) => {
				patched.push((await request.json()) as { title: string; objective: string; agentDefinitionId: string });
				return HttpResponse.json(session({ status: "Paused", title: "Renamed" }));
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-edit"));
		fireEvent.change(await screen.findByTestId("edit-work-session-title"), { target: { value: "Renamed" } });
		fireEvent.click(screen.getByTestId("edit-work-session-submit"));

		await waitFor(() => expect(patched).toHaveLength(1));
		expect(patched[0]).toEqual({ title: "Renamed", objective: "Compare the options", agentDefinitionId: agentId });
	});

	it("locks the objective while a step is live", async () => {
		routes(session({ status: "Running" }));
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-edit"));

		expect(((await screen.findByTestId("edit-work-session-objective")) as HTMLTextAreaElement).disabled).toBe(true);
		expect((screen.getByTestId("edit-work-session-title") as HTMLInputElement).disabled).toBe(false);
	});

	it("deletes only after the operator confirms, then leaves the route", async () => {
		routes(session({ status: "Paused" }));
		let deleted = 0;
		server.use(
			http.delete(localApiPath(`work-sessions/${sessionId}`), () => {
				deleted += 1;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		confirmResult.value = false;
		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-delete"));
		await waitFor(() => expect(confirmSpy).toHaveBeenCalled());
		expect(deleted).toBe(0);
		expect(navigateSpy).not.toHaveBeenCalled();

		confirmResult.value = true;
		fireEvent.click(screen.getByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-delete"));

		await waitFor(() => expect(deleted).toBe(1));
		// The delete also removes the owned conversation, so the route must not stay open on it.
		await waitFor(() => expect(navigateSpy).toHaveBeenCalledWith({ to: "/work-sessions" }));
	});

	it("keeps the operator on the page and explains a refused delete", async () => {
		routes(session({ status: "Running" }));
		server.use(
			problemDetailsRoute("delete", `work-sessions/${sessionId}`, 409, { detail: "Cancel the session before deleting it." }),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-delete"));

		const alert = await screen.findByTestId("work-session-delete-error");
		expect(alert.textContent).toContain("Cancel the session before deleting it.");
		expect(navigateSpy).not.toHaveBeenCalled();
	});

	// Heading navigation has to land somewhere on every state of this page: the session's own title once it is loaded,
	// and the navigation label while there is no name to show. One h1 either way, never two.
	it("carries exactly one h1 while the session loads and once it has", async () => {
		routes();
		// No router: neither state under test renders a `Link`, and a RouterProvider paints nothing on the first pass,
		// which would hide the pending tree this case is about.
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		// Read synchronously, before the session query can settle: this is the pending tree, no waiting involved.
		const pending = screen.getAllByRole("heading", { level: 1 });
		expect(pending).toHaveLength(1);
		expect(pending[0]?.textContent).toBe("Work Sessions");

		// Waited for by test id, not by role: the pending h1 above would satisfy a role query straight away.
		await screen.findByTestId("work-session-title");
		const loaded = screen.getAllByRole("heading", { level: 1 });
		expect(loaded).toHaveLength(1);
		expect(loaded[0]?.textContent).toBe("Survey the vector-store options");
	});

	it("offers a way back when the session cannot be loaded", async () => {
		// The feeds go out with the mount and are only stopped afterwards by the missing detail — the case below is
		// what pins that they stop, and they are declared here because this case makes those reads too.
		server.use(
			problemDetailsRoute("get", `work-sessions/${sessionId}`, 404, { detail: "no such session" }),
			...subordinateFeedRoutes(),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />, { withRouter: true });

		const alert = await screen.findByTestId("work-session-detail-error");
		expect(alert.textContent).toContain("no such session");
		expect(screen.getByTestId("work-session-detail-back")).toBeDefined();
	});

	it("stops polling subordinate feeds after the session detail is missing", async () => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		try {
			const requests = new Map<string, number>();
			const missing = (path: string) =>
				http.get(localApiPath(`work-sessions/${sessionId}${path}`), () => {
					requests.set(path, (requests.get(path) ?? 0) + 1);
					return HttpResponse.json(
						{ type: "about:blank", title: "Error", status: 404, detail: "no such session" },
						{ status: 404, headers: { "content-type": "application/problem+json" } },
					);
				});

			server.use(...["", "/tasks", "/findings", "/artifacts", "/checkpoints", "/events"].map(missing));
			hubMock.connection.invoke.mockRejectedValue(new Error("subscription refused"));

			renderDetail(<WorkSessionDetailPage sessionId={sessionId} />, { withRouter: true });

			const alert = await screen.findByTestId("work-session-detail-error");
			expect(alert.textContent).toContain("no such session");
			expect(screen.getByTestId("work-session-detail-back")).toBeDefined();

			await vi.advanceTimersByTimeAsync(6_100);

			for (const path of ["", "/tasks", "/findings", "/artifacts", "/checkpoints", "/events"]) {
				expect(requests.get(path), path || "/detail").toBe(1);
			}
		} finally {
			vi.useRealTimers();
		}
	});
	// An operator who switched WorkSessions:Enabled off still has this URL in their history, and the session GET is
	// then a bodyless 404 — rendered as "This work session could not be loaded", the same words a genuinely missing
	// id gets. The capability read is what tells them apart.
	it("says the feature is switched off on this node instead of reporting a missing session", async () => {
		let sessionReads = 0;
		server.use(
			capabilityRoute(false),
			http.get(localApiPath(`work-sessions/${sessionId}`), () => {
				sessionReads += 1;
				return new HttpResponse(null, { status: 404 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />, { withRouter: true });

		const disabled = await screen.findByTestId("work-sessions-disabled");
		expect(disabled.textContent).toBe("Work sessions are disabled by this node's runtime configuration.");
		expect(screen.queryByTestId("work-session-detail-error")).toBeNull();
		// Gated, not merely hidden: a disabled node must not be asked for data it will refuse, and the hub — a second
		// path onto the same disabled family — must not be acquired either.
		await waitFor(() => expect(sessionReads).toBe(0));
		expect(hubMock.acquire).not.toHaveBeenCalled();
	});

	// A capability call that FAILED says nothing about the switch, so reporting it as "switched off" would be a guess.
	it("reports a failed capability check as an error, not as a switched-off feature", async () => {
		server.use(problemDetailsRoute("get", "work-sessions/capability", 500, { detail: "the node is unreachable" }));
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />, { withRouter: true });

		const alert = await screen.findByTestId("work-sessions-disabled");
		expect(alert.textContent).toContain("the node is unreachable");
		expect(alert.textContent).not.toContain("disabled by this node's runtime configuration");
	});
});
