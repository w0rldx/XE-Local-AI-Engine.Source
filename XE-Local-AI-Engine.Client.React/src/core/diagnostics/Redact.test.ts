import { describe, expect, it } from "vitest";

import { REDACTED, redactConsoleArgs, redactHeaders, redactUrl, toNetworkEntry } from "@/core/diagnostics/Redact";

describe("redactHeaders", () => {
	it("strips Authorization/Bearer values regardless of casing", () => {
		const result = redactHeaders({
			Authorization: "Bearer super-secret-token",
			authorization: "Bearer another",
			"Content-Type": "application/json",
		});

		expect(result["Authorization"]).toBe(REDACTED);
		expect(result["authorization"]).toBe(REDACTED);
		expect(result["Content-Type"]).toBe("application/json");
		expect(JSON.stringify(result)).not.toContain("super-secret-token");
	});
});

describe("redactUrl", () => {
	it("strips token-bearing query params but keeps the rest", () => {
		const result = redactUrl("/api/local/v1/thing?access_token=secret123&token=abc&page=2");

		expect(result).not.toContain("secret123");
		expect(result).not.toContain("abc");
		expect(result).toContain("page=2");
		// The replacement marker survives URL-encoding of the brackets (`%5BREDACTED%5D`).
		expect(result).toContain("REDACTED");
	});

	it("scrubs a Bearer token embedded in an absolute URL", () => {
		const result = redactUrl("https://example.test/cb?bearer=eyJhbGciOi");
		expect(result).not.toContain("eyJhbGciOi");
	});
});

describe("toNetworkEntry", () => {
	it("drops request/response bodies for chat endpoints, keeping method/url/status/traceId", () => {
		const entry = toNetworkEntry({
			transport: "axios",
			method: "post",
			url: "/api/local/v1/chat/messages",
			status: 500,
			traceId: "0af7651916cd43dd8448eb211c80319c",
			requestBody: { text: "my-private-conversation-body" },
			responseBody: { reply: "another-secret-reply" },
		});

		const serialized = JSON.stringify(entry);
		expect(serialized).not.toContain("my-private-conversation-body");
		expect(serialized).not.toContain("another-secret-reply");
		expect(entry).not.toHaveProperty("requestBody");
		expect(entry).not.toHaveProperty("responseBody");
		expect(entry.method).toBe("POST");
		expect(entry.status).toBe(500);
		expect(entry.traceId).toBe("0af7651916cd43dd8448eb211c80319c");
		expect(entry.url).toBe("/api/local/v1/chat/messages");
	});
});

describe("redactConsoleArgs", () => {
	it("masks password/token fields inside object args (deep)", () => {
		const result = redactConsoleArgs([
			"login failed",
			{ password: "hunter2", authorization: "Bearer leak", nested: { token: "deep-secret" } },
		]);

		const serialized = JSON.stringify(result);
		expect(serialized).not.toContain("hunter2");
		expect(serialized).not.toContain("leak");
		expect(serialized).not.toContain("deep-secret");
		expect(serialized).toContain(REDACTED);
		expect(result[0]).toBe("login failed");
	});

	it("scrubs a Bearer token inside a string arg", () => {
		const result = redactConsoleArgs(["auth header was Bearer abc.def.ghi"]);
		expect(JSON.stringify(result)).not.toContain("abc.def.ghi");
	});
});
