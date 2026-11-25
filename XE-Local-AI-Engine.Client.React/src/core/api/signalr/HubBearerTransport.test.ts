import { HttpTransportType } from "@microsoft/signalr";
import { beforeEach, describe, expect, it, vi } from "vitest";

interface RecordedSocket {
	url: string;
	protocols: string | string[] | undefined;
}

const recorded: RecordedSocket[] = [];

// Stands in for the browser WebSocket the subclass extends; it records what the subclass hands to its base.
class RecordingWebSocket {
	constructor(url: string | URL, protocols?: string | string[]) {
		recorded.push({ url: String(url), protocols });
	}
}

let hubBearerTransportOptions: typeof import("@/core/api/signalr/HubBearerTransport").hubBearerTransportOptions;
let HubBearerWebSocket: NonNullable<typeof hubBearerTransportOptions.WebSocket>;

describe("hubBearerTransportOptions", () => {
	beforeEach(async () => {
		recorded.length = 0;
		// The subclass binds its base class when its module evaluates, so stub first, then load a fresh module.
		vi.stubGlobal("WebSocket", RecordingWebSocket);
		vi.resetModules();
		({ hubBearerTransportOptions } = await import("@/core/api/signalr/HubBearerTransport"));
		if (!hubBearerTransportOptions.WebSocket) {
			throw new Error("The options offer no WebSocket although the environment has one.");
		}
		HubBearerWebSocket = hubBearerTransportOptions.WebSocket;
	});

	it("moves the access token from the socket URL into the subprotocol list after the marker", () => {
		new HubBearerWebSocket("ws://h/api/local/v1/chat/hub?id=abc&access_token=a.b-c_d");

		expect(recorded).toEqual([{ url: "ws://h/api/local/v1/chat/hub?id=abc", protocols: ["xe.bearer", "a.b-c_d"] }]);
	});

	it("refuses a token outside the subprotocol token characters with a fixed message that never quotes it", () => {
		let thrown: unknown;
		try {
			new HubBearerWebSocket("ws://h/api/local/v1/chat/hub?id=abc&access_token=a%2Bb%20c");
		} catch (error) {
			thrown = error;
		}

		expect(thrown).toBeInstanceOf(Error);
		expect((thrown as Error).message).toBe("The hub access token is not a valid WebSocket subprotocol.");
		expect((thrown as Error).message).not.toContain("a+b c");
		expect(recorded).toEqual([]);
	});

	it("offers no subprotocol when the URL carries no token", () => {
		new HubBearerWebSocket("ws://h/api/local/v1/chat/hub?id=abc");

		expect(recorded).toEqual([{ url: "ws://h/api/local/v1/chat/hub?id=abc", protocols: [] }]);
	});

	it("allows only WebSockets and long polling, never server-sent events", () => {
		expect(hubBearerTransportOptions.transport).toBe(HttpTransportType.WebSockets | HttpTransportType.LongPolling);
		expect(hubBearerTransportOptions.transport & HttpTransportType.ServerSentEvents).toBe(0);
	});
});

describe("hubBearerTransportOptions without a WebSocket API", () => {
	beforeEach(async () => {
		vi.stubGlobal("WebSocket", undefined);
		vi.resetModules();
		({ hubBearerTransportOptions } = await import("@/core/api/signalr/HubBearerTransport"));
	});

	it("loads and offers long polling alone, with no WebSocket constructor", () => {
		expect(hubBearerTransportOptions.transport).toBe(HttpTransportType.LongPolling);
		expect("WebSocket" in hubBearerTransportOptions).toBe(false);
	});
});
