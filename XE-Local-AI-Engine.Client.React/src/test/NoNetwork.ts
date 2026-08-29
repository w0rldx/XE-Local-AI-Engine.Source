// Vitest setup: fail fast on any network call from a test file that did not install MSW.
//
// This replaces the suite-wide MSW server that used to run for all 338 files (see src/test/UseMswServer.ts,
// which now installs it per consumer file). What has to survive that move is the load-bearing half of the old
// `onUnhandledRequest: "error"`: a request nobody stubbed must fail loudly instead of reaching the machine's
// real network, where it passes for the wrong reason on a developer box and hangs in CI.
//
// The guard must be transport-COMPLETE, not fetch-only. MSW intercepted Fetch, XMLHttpRequest, Node's
// ClientRequest and WebSocket; the generated SDK goes over axios, which selects its XHR adapter under jsdom,
// and SignalR opens a WebSocket, so a fetch-only replacement would let both straight through. Hence stubs for
// all four — and no MSW import, since the whole point is that a file which never touches the network pays
// nothing.
//
// `fetch` REJECTS rather than throwing synchronously, matching the shape MSW's unhandled-request error had:
// a caller that catches its own request errors keeps behaving as it did. The others throw where they are
// called, which is what axios, SignalR and node callers turn into a rejection anyway.
//
// A file that calls `setupMswServer()` gets the real transports back for its duration; MSW installs its own
// interceptors over them and keeps `onUnhandledRequest: "error"` there.

import http from "node:http";
import https from "node:https";

const UNEXPECTED_CALL = "unexpected network call in test — install MSW via setupMswServer() or mock the transport";

function guardError(transport: string, target?: unknown): Error {
	return new Error(`${UNEXPECTED_CALL} [${transport}${target === undefined ? "" : ` ${String(target)}`}]`);
}

function refuse(transport: string, target?: unknown): never {
	throw guardError(transport, target);
}

function fetchTarget(input: RequestInfo | URL): string {
	return typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
}

// Vitest shares Node builtin modules between the test files of a worker, so a stub installed by an earlier file
// can still be in place when this one captures "the real" transport. Each stub carries what it replaced, which
// keeps both the capture and the install idempotent whatever ran before.
interface Stubbed {
	xeReplacedTransport?: unknown;
}

function realTransport<T>(current: T): T {
	return ((current as Stubbed).xeReplacedTransport as T | undefined) ?? current;
}

function stubFor<T>(replaced: T, stub: object): T {
	return Object.assign(stub, { xeReplacedTransport: replaced }) as unknown as T;
}

// `import … from "node:http"` is the mutable CJS exports object at runtime (MSW patches these same two
// properties), but TypeScript types it as a read-only namespace.
interface RequestModule {
	request: typeof http.request;
	get: typeof http.get;
}
const httpModule = http as unknown as RequestModule;
const httpsModule = https as unknown as RequestModule;

// Absent in the node environment, which a third of the suite runs in.
const xhrPrototype = typeof XMLHttpRequest === "undefined" ? undefined : XMLHttpRequest.prototype;

const real = {
	fetch: realTransport(globalThis.fetch),
	xhrOpen: xhrPrototype && realTransport(xhrPrototype.open),
	xhrSend: xhrPrototype && realTransport(xhrPrototype.send),
	webSocket: typeof WebSocket === "undefined" ? undefined : realTransport(globalThis.WebSocket),
	httpRequest: realTransport(httpModule.request),
	httpGet: realTransport(httpModule.get),
	httpsRequest: realTransport(httpsModule.request),
	httpsGet: realTransport(httpsModule.get),
};

export function installNetworkGuard(): void {
	globalThis.fetch = stubFor(real.fetch, (input: RequestInfo | URL) => Promise.reject(guardError("fetch", fetchTarget(input))));
	if (xhrPrototype && real.xhrOpen && real.xhrSend) {
		xhrPrototype.open = stubFor(real.xhrOpen, (_method: string, url: string | URL) => refuse("XMLHttpRequest.open", url));
		xhrPrototype.send = stubFor(real.xhrSend, () => refuse("XMLHttpRequest.send"));
	}
	if (real.webSocket) {
		// A class rather than an arrow, because callers reach this one through `new WebSocket(...)`.
		globalThis.WebSocket = stubFor(
			real.webSocket,
			class {
				constructor(url: string | URL) {
					refuse("WebSocket", url);
				}
			},
		);
	}
	httpModule.request = stubFor(real.httpRequest, () => refuse("http.request"));
	httpModule.get = stubFor(real.httpGet, () => refuse("http.get"));
	httpsModule.request = stubFor(real.httpsRequest, () => refuse("https.request"));
	httpsModule.get = stubFor(real.httpsGet, () => refuse("https.get"));
}

export function restoreRealTransports(): void {
	globalThis.fetch = real.fetch;
	if (xhrPrototype && real.xhrOpen && real.xhrSend) {
		xhrPrototype.open = real.xhrOpen;
		xhrPrototype.send = real.xhrSend;
	}
	if (real.webSocket) {
		globalThis.WebSocket = real.webSocket;
	}
	httpModule.request = real.httpRequest;
	httpModule.get = real.httpGet;
	httpsModule.request = real.httpsRequest;
	httpsModule.get = real.httpsGet;
}

installNetworkGuard();
