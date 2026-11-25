// @vitest-environment jsdom
import type { LogLevel } from "@microsoft/signalr";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The real @microsoft/signalr on its browser branch (no mock): the only place the token-in-URL behaviour and the
// console lines it can produce are observable. Network and sockets are recording fakes.

const token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJvcGVyYXRvciJ9.c2lnbmF0dXJl";
const hubUrl = "/api/local/v1/chat/hub";
const recordSeparator = "\u001e";

interface RecordedRequest {
	method: string;
	url: string;
	authorization: string | undefined;
}

interface RecordedSocket {
	url: string;
	protocols: string | string[] | undefined;
}

let socketsOpen = true;
let negotiations = 0;
let firstPollAnswered = false;
let requests: RecordedRequest[] = [];
let sockets: RecordedSocket[] = [];
let eventSourcesBuilt = 0;
const pendingPollFrames: string[] = [];
let wakePoll: (() => void) | undefined;

// Answers a SignalR JSON frame the way the hub would: the handshake with `{}`, an invocation with its first argument.
function answer(frame: string): string | undefined {
	const message = JSON.parse(frame) as { protocol?: string; type?: number; invocationId?: string; arguments?: unknown[] };
	if (message.protocol === "json") {
		return `{}${recordSeparator}`;
	}
	if (message.type === 1) {
		return `${JSON.stringify({ type: 3, invocationId: message.invocationId, result: message.arguments?.[0] })}${recordSeparator}`;
	}
	return undefined;
}

function answers(payload: string): string[] {
	return payload
		.split(recordSeparator)
		.filter((frame) => frame.length > 0)
		.map(answer)
		.filter((reply): reply is string => reply !== undefined);
}

class RecordingWebSocket {
	static readonly CONNECTING = 0;
	static readonly OPEN = 1;
	static readonly CLOSING = 2;
	static readonly CLOSED = 3;

	binaryType = "blob";
	readyState = RecordingWebSocket.CONNECTING;
	onopen: ((event: unknown) => void) | null = null;
	onmessage: ((event: { data: string }) => void) | null = null;
	onclose: ((event: { code: number; reason: string; wasClean: boolean }) => void) | null = null;
	onerror: ((event: unknown) => void) | null = null;

	constructor(url: string | URL, protocols?: string | string[]) {
		sockets.push({ url: String(url), protocols });
		queueMicrotask(() => {
			if (socketsOpen) {
				this.readyState = RecordingWebSocket.OPEN;
				this.onopen?.({});
			} else {
				this.readyState = RecordingWebSocket.CLOSED;
				this.onclose?.({ code: 1006, reason: "", wasClean: false });
			}
		});
	}

	send(data: string): void {
		for (const reply of answers(data)) {
			queueMicrotask(() => this.onmessage?.({ data: reply }));
		}
	}

	close(): void {
		this.readyState = RecordingWebSocket.CLOSED;
		queueMicrotask(() => this.onclose?.({ code: 1000, reason: "", wasClean: true }));
	}
}

class RecordingEventSource {
	constructor() {
		eventSourcesBuilt += 1;
	}
}

function respond(status: number, body: string): Response {
	return new Response(body, { status });
}

// Negotiate v1 with a connectionToken distinct from the connectionId, then long polling that parks until a send queues a reply.
async function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
	const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
	const method = init?.method ?? "GET";
	requests.push({ method, url, authorization: new Headers(init?.headers).get("Authorization") ?? undefined });

	if (method === "POST" && url.includes("/negotiate")) {
		negotiations += 1;
		return respond(
			200,
			JSON.stringify({
				negotiateVersion: 1,
				connectionId: `connection-id-${negotiations}`,
				connectionToken: `connection-token-${negotiations}`,
				availableTransports: [
					{ transport: "WebSockets", transferFormats: ["Text", "Binary"] },
					{ transport: "ServerSentEvents", transferFormats: ["Text"] },
					{ transport: "LongPolling", transferFormats: ["Text", "Binary"] },
				],
			}),
		);
	}
	if (method === "POST") {
		pendingPollFrames.push(...answers(String(init?.body ?? "")));
		wakePoll?.();
		return respond(200, "");
	}
	if (method === "DELETE") {
		return respond(202, "");
	}
	if (!firstPollAnswered) {
		firstPollAnswered = true;
		return respond(200, "");
	}
	if (pendingPollFrames.length === 0) {
		await new Promise<void>((resolve, reject) => {
			wakePoll = resolve;
			init?.signal?.addEventListener("abort", () => reject(new DOMException("The poll was aborted.", "AbortError")));
		});
	}
	return respond(200, pendingPollFrames.splice(0).join(""));
}

const consoleMethods = ["error", "warn", "info", "log"] as const;
let consoleSpies: { mock: { calls: unknown[][] } }[] = [];

function consoleText(): string[] {
	return consoleSpies.flatMap((spy) => spy.mock.calls.map((call) => call.map(String).join(" ")));
}

let signalr: typeof import("@microsoft/signalr");
let hubBearerTransportOptions: typeof import("@/core/api/signalr/HubBearerTransport").hubBearerTransportOptions;
let savedRelease: PropertyDescriptor | undefined;

describe("hubBearerTransportOptions with the real SignalR client in a browser", () => {
	beforeEach(async () => {
		socketsOpen = true;
		negotiations = 0;
		firstPollAnswered = false;
		requests = [];
		sockets = [];
		eventSourcesBuilt = 0;
		pendingPollFrames.length = 0;
		wakePoll = undefined;
		// The library takes its Node branch (an Authorization header on the socket) when process.release says node.
		savedRelease = Object.getOwnPropertyDescriptor(process, "release");
		Object.defineProperty(process, "release", { value: { name: "browser" }, configurable: true });
		vi.stubGlobal("fetch", fakeFetch);
		vi.stubGlobal("WebSocket", RecordingWebSocket);
		vi.stubGlobal("EventSource", RecordingEventSource);
		consoleSpies = consoleMethods.map((method) => vi.spyOn(console, method).mockImplementation(() => undefined));
		vi.resetModules();
		signalr = await import("@microsoft/signalr");
		({ hubBearerTransportOptions } = await import("@/core/api/signalr/HubBearerTransport"));
	});

	afterEach(() => {
		if (savedRelease) {
			Object.defineProperty(process, "release", savedRelease);
		}
	});

	async function connectAndInvoke(logLevel: LogLevel, withBearerOptions: boolean): Promise<unknown> {
		const connection = new signalr.HubConnectionBuilder()
			.withUrl(hubUrl, {
				accessTokenFactory: () => token,
				...(withBearerOptions ? hubBearerTransportOptions : {}),
			})
			.configureLogging(logLevel)
			.build();
		await connection.start();
		const result = await connection.invoke("Echo", "pong");
		await connection.stop();
		return result;
	}

	it("opens the socket with only the connection id in its URL and the token in the subprotocol list", async () => {
		const result = await connectAndInvoke(signalr.LogLevel.Warning, true);

		expect(result).toBe("pong");
		expect(sockets).toHaveLength(1);
		expect(sockets[0]?.url).toMatch(/\/api\/local\/v1\/chat\/hub\?id=connection-token-1$/);
		expect(sockets[0]?.url).not.toContain("access_token");
		expect(sockets[0]?.url).not.toContain(token);
		expect(sockets[0]?.protocols).toEqual(["xe.bearer", token]);
		const negotiate = requests.find((request) => request.url.includes("/negotiate"));
		expect(negotiate?.authorization).toBe(`Bearer ${token}`);
		expect(consoleText().some((line) => line.includes(token) || line.includes("access_token"))).toBe(false);
	});

	it("still prints the token URL at Information, which is why both builders log at Warning", async () => {
		await connectAndInvoke(signalr.LogLevel.Information, true);

		const infoLines = consoleSpies[consoleMethods.indexOf("info")]?.mock.calls.map((call) => call.map(String).join(" ")) ?? [];
		expect(infoLines.some((line) => line.includes("access_token="))).toBe(true);
	});

	it("puts the token in the socket URL without the options, proving the browser branch ran", async () => {
		await connectAndInvoke(signalr.LogLevel.Warning, false);

		expect(sockets).toHaveLength(1);
		expect(sockets[0]?.url).toContain("access_token=");
		expect(sockets[0]?.protocols).toBeUndefined();
	});

	it("falls back to long polling with the header, never to server-sent events, when the socket fails", async () => {
		socketsOpen = false;

		const result = await connectAndInvoke(signalr.LogLevel.Warning, true);

		expect(result).toBe("pong");
		expect(sockets).toHaveLength(1);
		expect(eventSourcesBuilt).toBe(0);
		const polls = requests.filter((request) => request.method === "GET");
		expect(polls.length).toBeGreaterThan(0);
		expect(polls.every((poll) => poll.url.includes("id=connection-token-2&"))).toBe(true);
		expect(requests.every((request) => request.authorization === `Bearer ${token}`)).toBe(true);
		expect(requests.some((request) => request.url.includes(token) || request.url.includes("access_token"))).toBe(false);
		expect(consoleText().some((line) => line.includes(token) || line.includes("access_token"))).toBe(false);
	});
});
