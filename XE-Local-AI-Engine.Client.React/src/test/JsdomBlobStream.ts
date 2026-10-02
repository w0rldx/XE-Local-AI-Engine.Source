// Vitest setup: give jsdom's `Blob` the `stream()` the Fetch API requires.
//
// jsdom (30.0.1) implements `Blob.prototype.arrayBuffer()` and `text()` but not `stream()`. Node's `Response`
// constructor (undici) treats anything with `arrayBuffer()` and a `Blob` tag as a Blob and calls `stream()` on it.
// Node 22, the CI runtime, throws `TypeError: object.stream is not a function`; Node 24 tolerates the gap, so the
// failure only shows in CI.
//
// The path that hits it is axios' XHR adapter with `responseType: "blob"` under MSW: the XHR interceptor builds the
// mocked response as a jsdom `Blob`, then wraps it in a `Response` for MSW's lifecycle events — for a 404 as much as
// for a 200. Every test that loads image bytes (useImageObjectUrl) failed under Node 22 for it.
//
// Only the missing method is added; a jsdom that ships `stream()` is left alone. Absent in the node environment.

if (typeof Blob !== "undefined" && typeof Blob.prototype.stream !== "function") {
	Blob.prototype.stream = function stream(this: Blob): ReadableStream<Uint8Array<ArrayBuffer>> {
		const blob = this;
		return new ReadableStream<Uint8Array<ArrayBuffer>>({
			async start(controller) {
				controller.enqueue(new Uint8Array(await blob.arrayBuffer()));
				controller.close();
			},
		});
	};
}
