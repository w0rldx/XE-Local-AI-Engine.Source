// @vitest-environment jsdom

import { describe, expect, it } from "vitest";

// Direct check for src/test/JsdomBlobStream.ts. The image tests that exposed the gap threw only under Node 22; on
// Node 24 and later the body silently becomes "[object Blob]", so this check names the polyfill going missing.

describe("jsdom Blob.stream polyfill", () => {
	it("streams the blob's bytes", async () => {
		expect(typeof Blob.prototype.stream).toBe("function");
		expect(await new Response(new Blob(["png"]).stream()).text()).toBe("png");
	});
});
