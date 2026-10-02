// @vitest-environment jsdom

import { describe, expect, it } from "vitest";

// Direct check for src/test/JsdomBlobStream.ts. The image tests that exposed the gap only fail under Node 22, so
// on a Node 24 developer box nothing else would notice the polyfill going missing.

describe("jsdom Blob.stream polyfill", () => {
	it("streams the blob's bytes", async () => {
		expect(typeof Blob.prototype.stream).toBe("function");
		expect(await new Response(new Blob(["png"]).stream()).text()).toBe("png");
	});
});
