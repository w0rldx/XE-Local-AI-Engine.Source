// @vitest-environment jsdom

// The command page end to end against stubbed REST routes (MSW), a faked shared-hub seam per hub path, and the repo's
// capture fake in place of the microphone. What it pins: Enter posts the intent with the right `utteredAt`, a started
// run follows the graph hub to its terminal status, NotMatched falls through to the default agent over the chat hub,
// the two refusals read as notices, and dictation appends committed text but never partials.

import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import i18next from "i18next";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { encodePcmFrame } from "@/core/audio/capture/CaptureSource";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import { CommandPage } from "@/features/command/pages/CommandPage";
import {
	agentNodeRunDetail,
	graphWorkflowRun,
	graphWorkflowRunSummary,
	graphWorkflowTestGuid,
	graphWorkflowTestIds,
	makeNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import type { FakeCaptureSource } from "@/features/transcription/capture/FakeCaptureSource";
import de from "@/locales/de.json";
import { localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

type Handler = (payload: unknown) => void;

const hubs = vi.hoisted(() => {
	function makeHub() {
		const handlers = new Map<string, Set<Handler>>();
		const connection = {
			state: "Connected",
			on: vi.fn((event: string, handler: Handler) => {
				const set = handlers.get(event) ?? new Set<Handler>();
				set.add(handler);
				handlers.set(event, set);
			}),
			off: vi.fn((event: string, handler: Handler) => handlers.get(event)?.delete(handler)),
			// The real `invoke` always returns a promise; the page chains `.catch` on it.
			invoke: vi.fn(async (..._args: unknown[]): Promise<unknown> => undefined),
			stream: vi.fn(),
		};
		const handle = {
			connection,
			whenStarted: Promise.resolve(),
			onReconnected: vi.fn(() => () => undefined),
			onReconnecting: vi.fn(() => () => undefined),
			onClosed: vi.fn(() => () => undefined),
			release: vi.fn(),
		};
		const emit = (event: string, payload: unknown): void => {
			for (const handler of handlers.get(event) ?? []) {
				handler(payload);
			}
		};
		return { connection, handle, emit };
	}
	const byPath = new Map<string, ReturnType<typeof makeHub>>();
	return {
		byPath,
		get(path: string): ReturnType<typeof makeHub> {
			let hub = byPath.get(path);
			if (hub === undefined) {
				hub = makeHub();
				byPath.set(path, hub);
			}
			return hub;
		},
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: (path: string) => hubs.get(path).handle,
}));

const microphones = vi.hoisted(() => ({ created: [] as FakeCaptureSource[] }));

// jsdom has no AudioContext or getUserMedia; the repo's capture fake stands in for the microphone.
vi.mock("@/core/audio/capture/MicrophoneCaptureSource", async () => {
	const { FakeCaptureSource } = await import("@/features/transcription/capture/FakeCaptureSource");
	return {
		MicrophoneCaptureSource: class extends FakeCaptureSource {
			constructor(channel: "mono") {
				super(channel);
				microphones.created.push(this);
			}
		},
	};
});

setupMswServer();

const runId = graphWorkflowTestIds.run;
const definitionId = graphWorkflowTestIds.definition;
const conversationId = "c0c0c0c0-0000-4000-8000-000000000001";
const sessionId = "d1d1d1d1-0000-4000-8000-000000000002";

function intentRoute(body: unknown): { requests: Record<string, unknown>[] } {
	const requests: Record<string, unknown>[] = [];
	server.use(
		http.post(localApiPath("graph-workflows/intents/invoke"), async ({ request }) => {
			requests.push((await request.json()) as Record<string, unknown>);
			return HttpResponse.json(body as Record<string, unknown>);
		}),
	);
	return { requests };
}

function dictationRoute(): { bodies: unknown[] } {
	const bodies: unknown[] = [];
	server.use(
		http.post(localApiPath("transcription/dictation-sessions"), async ({ request }) => {
			bodies.push(await request.json());
			return HttpResponse.json({ sessionId, startedAtUtc: Date.now(), lastSeq: 0 });
		}),
	);
	return { bodies };
}

function notMatched(): unknown {
	return { status: "NotMatched", matchedBy: "None", definitionId: null, definitionName: null, reason: "NoRouter", run: null };
}

function input(): HTMLTextAreaElement {
	return screen.getByRole("textbox", { name: "Command" }) as HTMLTextAreaElement;
}

function type(value: string): void {
	fireEvent.change(input(), { target: { value } });
}

function pressEnter(): void {
	fireEvent.keyDown(input(), { key: "Enter" });
}

async function renderPage(): Promise<void> {
	renderWithProviders(<CommandPage />, { withRouter: true });
	// The memory router mounts asynchronously.
	await screen.findByTestId("command-page");
}

beforeEach(() => {
	hubs.byPath.clear();
	microphones.created.length = 0;
	usePendingChatConversationStore.setState({ pendingConversationId: "" });
	localStorage.clear();
});

afterEach(async () => {
	// The app's real i18next instance is shared across the file, so a language switch must be undone.
	await i18next.changeLanguage("en");
});

describe("CommandPage", () => {
	it("starts the matched workflow and follows its run over the graph hub to a terminal status", async () => {
		const { requests } = intentRoute({
			status: "Started",
			matchedBy: "Alias",
			definitionId,
			definitionName: "Lights on",
			reason: null,
			run: graphWorkflowRun({ run: graphWorkflowRunSummary({ status: "Running" }) }),
		});
		let runStatus = "Running";
		server.use(
			http.get(localApiPath(`graph-workflows/runs/${runId}`), () =>
				HttpResponse.json(graphWorkflowRun({ run: graphWorkflowRunSummary({ status: runStatus }) })),
			),
		);
		await renderPage();

		type("  lights on ");
		pressEnter();

		expect((await screen.findByTestId("command-run")).textContent).toContain("Running Lights on");
		expect(input().value).toBe("");
		expect(requests).toHaveLength(1);
		expect(requests[0]?.["utterance"]).toBe("lights on");
		expect(typeof requests[0]?.["requestId"]).toBe("string");
		// Typed text is "said" on Enter: a fresh timestamp, not a stale one.
		expect(Math.abs(Date.parse(String(requests[0]?.["utteredAt"])) - Date.now())).toBeLessThan(60_000);

		const graphHub = hubs.get("graph-workflows/hub");
		await waitFor(() => expect(graphHub.connection.invoke).toHaveBeenCalledWith("SubscribeRun", runId, 0));
		const link = screen.getByRole("link", { name: "Open run" });
		expect(link.getAttribute("href")).toContain(`runId=${runId}`);
		expect(link.getAttribute("href")).toContain(`definitionId=${definitionId}`);

		runStatus = "Completed";
		act(() => graphHub.emit("graphWorkflowChanged", { runId, seq: 9, kind: "run" }));
		await waitFor(() => expect(screen.getByTestId("command-run-status").textContent).toBe("Completed"));
		expect(screen.getByTestId("command-run").textContent).not.toContain("Running Lights on");
	});

	it("names the first failed node and its reason when the run fails, never the node's output", async () => {
		const failedRun = graphWorkflowRun({
			run: graphWorkflowRunSummary({ status: "Failed" }),
			nodeRuns: [
				makeNodeRun({ id: graphWorkflowTestGuid(1), nodeKey: "start", kind: "Start", status: "Succeeded" }),
				// Failed later: the row names the node that failed FIRST.
				makeNodeRun({
					id: graphWorkflowTestGuid(3),
					nodeKey: "notify",
					kind: "Tool",
					status: "Failed",
					failureClass: "Permanent",
					startedAtUtc: 1_700_000_300_000,
				}),
				makeNodeRun({
					id: graphWorkflowTestGuid(2),
					nodeKey: "lights",
					kind: "Tool",
					status: "Failed",
					failureClass: "Permanent",
					startedAtUtc: 1_700_000_200_000,
				}),
			],
		});
		intentRoute({
			status: "Started",
			matchedBy: "Alias",
			definitionId,
			definitionName: "Lights on",
			reason: null,
			run: failedRun,
		});
		const nodeReads: string[] = [];
		server.use(
			http.get(localApiPath(`graph-workflows/runs/${runId}`), () => HttpResponse.json(failedRun)),
			http.get(localApiPath(`graph-workflows/runs/${runId}/nodes/:nodeKey`), ({ params }) => {
				nodeReads.push(String(params["nodeKey"]));
				return HttpResponse.json(
					agentNodeRunDetail({
						nodeKey: "lights",
						kind: "Tool",
						status: "Failed",
						failureClass: "Permanent",
						error: "action-failed",
						output: { status: "failed", attempt: 1, output: { text: "The bridge refused the scene." } },
					}),
				);
			}),
		);
		await renderPage();

		type("lights on");
		pressEnter();

		expect((await screen.findByTestId("command-run-failure")).textContent).toBe("lights: action-failed");
		expect(screen.getByTestId("command-run-status").textContent).toBe("Failed");
		expect(screen.getByTestId("command-run").textContent).not.toContain("The bridge refused the scene.");
		expect(nodeReads).toEqual(["lights"]);
	});

	it("falls through to the default agent when nothing matched, streams the reply and offers the conversation", async () => {
		intentRoute(notMatched());
		const created: unknown[] = [];
		server.use(
			http.post(localApiPath("chat/conversations"), async ({ request }) => {
				created.push(await request.json());
				return HttpResponse.json({
					conversationId,
					title: "what is the weather",
					userId: null,
					createdAtUtc: 1,
					lastSeenUtc: 1,
					purged: false,
					origin: "Local",
					isPinned: false,
					archived: false,
					memoryExcluded: false,
					messages: [],
				});
			}),
		);
		const chatHub = hubs.get("chat/hub");
		let observer: { next: (frame: unknown) => void; complete: () => void } | undefined;
		chatHub.connection.stream.mockImplementation(() => ({
			subscribe: (next: typeof observer) => {
				observer = next;
				return { dispose: () => undefined };
			},
		}));
		await renderPage();

		type("what is the weather");
		pressEnter();

		await waitFor(() => expect(chatHub.connection.stream).toHaveBeenCalled());
		expect(chatHub.connection.stream).toHaveBeenCalledWith("SendMessage", { conversationId, content: "what is the weather" });
		expect(created).toEqual([{ title: "what is the weather" }]);

		act(() => observer?.next({ type: "assistant-delta", delta: "It is " }));
		act(() => observer?.next({ type: "assistant-delta", delta: "sunny" }));
		expect(screen.getByTestId("command-reply").textContent).toContain("It is sunny");
		act(() => {
			observer?.next({ type: "assistant-completed", content: "It is sunny today." });
			observer?.complete();
		});
		await waitFor(() => expect(screen.getByTestId("command-reply").textContent).toContain("It is sunny today."));
		expect(chatHub.handle.release).toHaveBeenCalled();

		fireEvent.click(screen.getByRole("button", { name: "Open in chat" }));
		expect(usePendingChatConversationStore.getState().pendingConversationId).toBe(conversationId);
	});

	it("asks the user to say it again when the node refuses a stale utterance", async () => {
		server.use(
			problemDetailsRoute("post", "graph-workflows/intents/invoke", 409, {
				title: "Conflict",
				conflictType: "GraphWorkflowIntentExpired",
			} as never),
		);
		await renderPage();

		type("lights on");
		pressEnter();

		expect((await screen.findByTestId("command-refusal")).textContent).toBe("That took too long. Please say it again.");
	});

	it("shows a short notice when the node answers 404 because graph workflows are switched off", async () => {
		server.use(problemDetailsRoute("post", "graph-workflows/intents/invoke", 404, { title: "Not Found" }));
		await renderPage();

		type("lights on");
		pressEnter();

		expect((await screen.findByTestId("command-refusal")).textContent).toBe(
			"Commands are not available: Graph Workflows is switched off on this node.",
		);
	});

	it("dictates: pushes microphone frames, appends committed text, never partials, and carries when it was said", async () => {
		const startedAtUtc = Date.now() - 2_000;
		server.use(
			http.post(localApiPath("transcription/dictation-sessions"), () =>
				HttpResponse.json({ sessionId, startedAtUtc, lastSeq: 0 }),
			),
		);
		const { requests } = intentRoute(notMatched());
		server.use(http.post(localApiPath("chat/conversations"), () => HttpResponse.json({}, { status: 500 })));
		await renderPage();
		const transcriptionHub = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		const stopButton = await screen.findByRole("button", { name: "Stop dictation" });
		expect(transcriptionHub.connection.invoke).toHaveBeenCalledWith("SubscribeSession", sessionId, 0);

		const microphone = microphones.created[0];
		const pcm = new Int16Array([1, -1, 300]);
		microphone?.emit(pcm);
		expect(transcriptionHub.connection.invoke).toHaveBeenCalledWith("PushAudioFrame", sessionId, 0, encodePcmFrame(pcm));

		act(() => transcriptionHub.emit("transcriptionPartialUpdated", { sessionId, channel: "Mono", text: "turn the" }));
		expect(screen.getByTestId("command-partial").textContent).toBe("turn the");
		expect(input().value).toBe("");

		act(() =>
			transcriptionHub.emit("transcriptionSegmentCommitted", {
				sessionId,
				seq: 1,
				startMs: 1_500,
				endMs: 2_400,
				text: " Turn the lights on",
				channel: "Mono",
			}),
		);
		act(() =>
			transcriptionHub.emit("transcriptionSegmentCommitted", {
				sessionId,
				seq: 2,
				startMs: 2_600,
				endMs: 3_000,
				text: "please",
				channel: "Mono",
			}),
		);
		expect(input().value).toBe("Turn the lights on please");
		expect(screen.queryByTestId("command-partial")).toBeNull();

		fireEvent.click(stopButton);
		await waitFor(() => expect(transcriptionHub.connection.invoke).toHaveBeenCalledWith("EndSession", sessionId));
		expect(microphone?.stopped).toBe(true);
		act(() => transcriptionHub.emit("transcriptionSessionStatusChanged", { sessionId, status: "Completed", errorCode: null }));
		expect(await screen.findByRole("button", { name: "Start dictation" })).toBeTruthy();
		expect(transcriptionHub.handle.release).toHaveBeenCalled();

		pressEnter();
		await waitFor(() => expect(requests).toHaveLength(1));
		// The FIRST committed segment's wall-clock time: the session start plus that segment's offset.
		expect(requests[0]?.["utteredAt"]).toBe(new Date(startedAtUtc + 1_500).toISOString());
		expect(requests[0]?.["utterance"]).toBe("Turn the lights on please");
		expect(await screen.findByText("The assistant could not answer. Open the conversation to try again.")).toBeTruthy();
	});

	it("releases the microphone when the node ends the dictation on its own", async () => {
		server.use(
			http.post(localApiPath("transcription/dictation-sessions"), () =>
				HttpResponse.json({ sessionId, startedAtUtc: Date.now(), lastSeq: 0 }),
			),
		);
		await renderPage();
		const transcriptionHub = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		await screen.findByRole("button", { name: "Stop dictation" });
		const microphone = microphones.created[0];
		expect(microphone?.started).toBe(true);

		// The node ends the session itself (buffered-audio cap, inference failure): no Stop was pressed.
		act(() =>
			transcriptionHub.emit("transcriptionSessionStatusChanged", { sessionId, status: "Failed", errorCode: "inference" }),
		);

		expect(await screen.findByRole("button", { name: "Start dictation" })).toBeTruthy();
		expect(microphone?.stopped).toBe(true);
		expect(transcriptionHub.handle.release).toHaveBeenCalled();
		expect(transcriptionHub.connection.invoke).not.toHaveBeenCalledWith("EndSession", sessionId);
	});

	it("ends the dictation when the window loses focus, as a dismissed overlay does", async () => {
		server.use(
			http.post(localApiPath("transcription/dictation-sessions"), () =>
				HttpResponse.json({ sessionId, startedAtUtc: Date.now(), lastSeq: 0 }),
			),
		);
		await renderPage();
		const transcriptionHub = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		await screen.findByRole("button", { name: "Stop dictation" });
		const microphone = microphones.created[0];

		// The desktop shell hides the overlay on Escape or focus loss and cannot tell the page; the page sees the blur.
		fireEvent(window, new Event("blur"));

		await waitFor(() => expect(transcriptionHub.connection.invoke).toHaveBeenCalledWith("EndSession", sessionId));
		expect(microphone?.stopped).toBe(true);
	});

	it("opens no microphone when the node ends the session while the subscription is still pending", async () => {
		server.use(
			http.post(localApiPath("transcription/dictation-sessions"), () =>
				HttpResponse.json({ sessionId, startedAtUtc: Date.now(), lastSeq: 0 }),
			),
		);
		await renderPage();
		const transcriptionHub = hubs.get("transcription/hub");
		let subscribed: (() => void) | undefined;
		transcriptionHub.connection.invoke.mockImplementation(async (method: unknown): Promise<unknown> => {
			if (method === "SubscribeSession") {
				await new Promise<void>((resolve) => {
					subscribed = resolve;
				});
			}
			return undefined;
		});

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		await waitFor(() => expect(subscribed).toBeDefined());
		// The node ends the session (its own failure) before the subscription resolved.
		act(() =>
			transcriptionHub.emit("transcriptionSessionStatusChanged", { sessionId, status: "Failed", errorCode: "inference" }),
		);
		subscribed?.();

		expect(await screen.findByRole("button", { name: "Start dictation" })).toBeTruthy();
		await waitFor(() => expect(transcriptionHub.handle.release).toHaveBeenCalled());
		expect(microphones.created).toHaveLength(0);
	});

	it("opens no microphone when the window is dismissed while the session is still starting", async () => {
		let answer: (() => void) | undefined;
		const sessionAnswered = new Promise<void>((resolve) => {
			answer = resolve;
		});
		server.use(
			http.post(localApiPath("transcription/dictation-sessions"), async () => {
				await sessionAnswered;
				return HttpResponse.json({ sessionId, startedAtUtc: Date.now(), lastSeq: 0 });
			}),
		);
		await renderPage();
		const transcriptionHub = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		// The overlay is hidden (Escape, focus loss) before the node has answered: the page is still mounted.
		fireEvent(window, new Event("blur"));
		answer?.();

		await waitFor(() => expect(transcriptionHub.connection.invoke).toHaveBeenCalledWith("EndSession", sessionId));
		expect(transcriptionHub.connection.invoke).not.toHaveBeenCalledWith("SubscribeSession", sessionId, 0);
		expect(microphones.created).toHaveLength(0);
		expect(await screen.findByRole("button", { name: "Start dictation" })).toBeTruthy();
	});

	it("opens no microphone when the page unmounts while the session is still starting", async () => {
		let answer: (() => void) | undefined;
		const sessionAnswered = new Promise<void>((resolve) => {
			answer = resolve;
		});
		server.use(
			http.post(localApiPath("transcription/dictation-sessions"), async () => {
				await sessionAnswered;
				return HttpResponse.json({ sessionId, startedAtUtc: Date.now(), lastSeq: 0 });
			}),
		);
		const { unmount } = renderWithProviders(<CommandPage />, { withRouter: true });
		await screen.findByTestId("command-page");
		const transcriptionHub = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		unmount();
		answer?.();

		await waitFor(() => expect(transcriptionHub.connection.invoke).toHaveBeenCalledWith("EndSession", sessionId));
		expect(transcriptionHub.connection.invoke).not.toHaveBeenCalledWith("SubscribeSession", sessionId, 0);
		expect(microphones.created).toHaveLength(0);
		expect(transcriptionHub.handle.release).toHaveBeenCalled();
	});

	it("starts dictation in the UI language by default, then in the language picked, remembered for this browser", async () => {
		const { bodies } = dictationRoute();
		await renderPage();
		const transcriptionHub = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		await screen.findByRole("button", { name: "Stop dictation" });
		expect(bodies).toEqual([{ language: "en" }]);
		act(() => transcriptionHub.emit("transcriptionSessionStatusChanged", { sessionId, status: "Completed", errorCode: null }));
		await screen.findByRole("button", { name: "Start dictation" });

		fireEvent.click(screen.getByRole("radio", { name: "Auto" }));
		expect(localStorage.getItem("xe.command.dictationLanguage")).toBe("auto");
		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		await screen.findByRole("button", { name: "Stop dictation" });
		// Auto sends no language: the node detects it, as before the option existed.
		expect(bodies[1]).toEqual({});
	});

	it("defaults the dictation language to German on a German UI", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		const { bodies } = dictationRoute();
		await renderPage();

		expect((screen.getByRole("radio", { name: "DE" }) as HTMLInputElement).checked).toBe(true);
		fireEvent.click(screen.getByRole("button", { name: "Diktat starten" }));
		await screen.findByRole("button", { name: "Diktat beenden" });
		expect(bodies).toEqual([{ language: "de" }]);
	});

	it("listens to the hub pushes a dictation ignores, so SignalR does not warn, and stops listening when it ends", async () => {
		dictationRoute();
		await renderPage();
		const { connection, emit } = hubs.get("transcription/hub");

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));
		await screen.findByRole("button", { name: "Stop dictation" });
		const ignored = ["transcriptionCatchUpProgress", "transcriptionAdmissionClosed", "transcriptionSourceQuiet"];
		for (const event of ignored) {
			expect(connection.on).toHaveBeenCalledWith(event, expect.any(Function));
		}

		act(() => emit("transcriptionSessionStatusChanged", { sessionId, status: "Completed", errorCode: null }));
		await screen.findByRole("button", { name: "Start dictation" });
		for (const event of ignored) {
			const registered = connection.on.mock.calls.find(([name]) => name === event)?.[1];
			expect(connection.off).toHaveBeenCalledWith(event, registered);
		}
	});

	it("reports a node with transcription switched off inline and stays ready", async () => {
		server.use(problemDetailsRoute("post", "transcription/dictation-sessions", 404, { title: "Not Found" }));
		await renderPage();

		fireEvent.click(screen.getByRole("button", { name: "Start dictation" }));

		expect((await screen.findByRole("alert")).textContent).toBe("Transcription is switched off on this node.");
		expect(screen.getByRole("button", { name: "Start dictation" })).toBeTruthy();
		expect(microphones.created).toHaveLength(0);
	});

	it("clears the draft on Escape and adds a new line on Shift+Enter without sending", async () => {
		await renderPage();

		type("open my word");
		fireEvent.keyDown(input(), { key: "Enter", shiftKey: true });
		expect(input().value).toBe("open my word");

		fireEvent.keyDown(input(), { key: "Escape" });
		expect(input().value).toBe("");
	});
});
