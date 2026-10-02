import { describe, expect, it } from "vitest";

import {
	REDACTED,
	redactBreadcrumb,
	redactConsoleArgs,
	redactHeaders,
	redactString,
	redactUrl,
	toNetworkEntry,
} from "@/core/diagnostics/Redact";
import type { ErrorBreadcrumb } from "@/core/diagnostics/Types";

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

describe("redactBreadcrumb error case", () => {
	function errorCrumb(error: ErrorBreadcrumb["error"]): ErrorBreadcrumb {
		return { id: "crumb-1", timestamp: 0, category: "error", error };
	}

	it("scrubs a Bearer token from the error message, stack, and componentStack", () => {
		const redacted = redactBreadcrumb(
			errorCrumb({
				source: "boundary",
				message: "request failed with Authorization: Bearer abc.def.ghi",
				stack: "Error\n  at fetch (https://api.test/chat) Bearer eyJhbGciOi.payload.sig",
				componentStack: "at ChatPanel (Bearer nested.leak.token)",
			}),
		);

		const serialized = JSON.stringify(redacted);
		expect(serialized).not.toContain("abc.def.ghi");
		expect(serialized).not.toContain("eyJhbGciOi.payload.sig");
		expect(serialized).not.toContain("nested.leak.token");
		expect(serialized).toContain(REDACTED);
		expect(redacted.category).toBe("error");
	});

	it("leaves an error crumb without secrets untouched and preserves the source", () => {
		const redacted = redactBreadcrumb(errorCrumb({ source: "uncaught", message: "boom" })) as ErrorBreadcrumb;

		expect(redacted.error.message).toBe("boom");
		expect(redacted.error.source).toBe("uncaught");
		expect(redacted.error.stack).toBeUndefined();
		expect(redacted.error.componentStack).toBeUndefined();
	});
});

describe("redactString", () => {
	const jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

	it.each([
		["a Bearer token", "Authorization: Bearer abc.def-ghi", "Authorization: Bearer [REDACTED]"],
		["a JWT", `token was ${jwt} here`, "token was [REDACTED] here"],
		["an sk- key", "key sk-proj-AbCdEf0123456789xyz rejected", "key [REDACTED] rejected"],
		["a GitHub token", "ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "[REDACTED]"],
		["an AWS access key id", "aws AKIAIOSFODNN7EXAMPLE used", "aws [REDACTED] used"],
		["a Slack token", "slack xoxb-1234567890-abcdefghij posted", "slack [REDACTED] posted"],
		["a Google API key", "key=AIzaSyA1b2C3d4E5f6G7h8I9j0KlMnOpQrStUvW done", "key=[REDACTED] done"],
		["an Azure AccountKey", "AccountName=xe;AccountKey=abcdEFGHijklMNOPqrst+/uv==;", "AccountName=xe;[REDACTED];"],
		["a password assignment", "connect with password=hunter22, retry", "connect with password=[REDACTED], retry"],
		["a double-quoted value", 'login password="hunter22" sent', 'login password="[REDACTED]" sent'],
		["a single-quoted value after a colon", "password: 'hunter22' sent", "password: '[REDACTED]' sent"],
		["a JSON pair", '{"password":"hunter22"}', '{"password":"[REDACTED]"}'],
		["an apostrophe inside a double-quoted secret", 'password="it\'s a secret" end', 'password="[REDACTED]" end'],
		["a double quote inside a single-quoted secret", "password='say \"hi\" now' end", "password='[REDACTED]' end"],
		["JSON with an apostrophe in the value", '{"secret":"don\'t tell","n":1}', '{"secret":"[REDACTED]","n":1}'],
		[
			"a JSON line with two secret keys",
			'{"user":"bob","api_key": "abc-not-a-real-key","client_secret":"s3cr3t","port":8080}',
			'{"user":"bob","api_key": "[REDACTED]","client_secret":"[REDACTED]","port":8080}',
		],
		["a case-insensitive api key assignment", "Api-Key: plainvalue then", "Api-Key: [REDACTED] then"],
		["an all-letter Hugging Face token", "HF_TOKEN=hf_AbCdEfGhIjKlMnOpQrStUvWx set", "HF_TOKEN=[REDACTED] set"],
		["a dense run with 4+ digits", "key Zx9Kq2Lm8Np4Rt6Vw1Yb3Cd5 end", "key [REDACTED] end"],
		["a dense run inside a separated key", "x-key: live_Q7mT2pR9vK4wL8nB3cZ1 ok", "x-key: [REDACTED] ok"],
		["an e-mail", "contact jane.doe+xe@example.co.uk now", "contact [redacted-email] now"],
		["a Linux home path", "open /home/jane/projects/a.gguf failed", "open ~/projects/a.gguf failed"],
		["a macOS home path", "at /Users/Jane/Library/x.log", "at ~/Library/x.log"],
		["a Windows home path", "C:\\Users\\Jane\\AppData\\Local\\xe.log", "~\\AppData\\Local\\xe.log"],
		["a forward-slash Windows home path on a lowercase drive", "c:/users/jane/models", "~/models"],
		["a bare home dir", "cwd=/home/jane", "cwd=~"],
		["a Windows home path with a space", "C:\\Users\\Jane Doe\\AppData\\Local\\xe.log", "~\\AppData\\Local\\xe.log"],
		["a macOS home path with a space", "at /Users/Jane Doe/Library/x.log", "at ~/Library/x.log"],
		["a home dir followed by prose", "/home/jane is the user", "~ is the user"],
	])("redacts %s", (_label, input, expected) => {
		expect(redactString(input)).toBe(expected);
	});

	it("is idempotent across every shape", () => {
		const input = `Bearer x ${jwt} sk-proj-AbCdEf0123456789xyz jane@example.com /home/jane/a C:\\Users\\jane\\b /Users/Jane Doe/Library/c Zx9Kq2Lm8Np4Rt6Vw1Yb3Cd5 AKIAIOSFODNN7EXAMPLE secret: abcd1 {"password":"hunter22","api_key": 'k3yz9'} password="it's hidden" secret='say "hidden"' AccountKey=abcdEFGHijklMNOPqrst+/uv==`;
		const once = redactString(input);

		expect(redactString(once)).toBe(once);
		expect(once).not.toContain("jane");
		expect(once).not.toContain("Doe");
		expect(once).not.toContain("hunter22");
		expect(once).not.toContain("k3yz9");
		expect(once).not.toContain("hidden");
	});

	it.each([
		"Failed to load model bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M",
		"see https://hf.co/bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF/resolve/main/model.gguf",
		"GET /api/local/v1/chat/conversations/3fa85f64-5717-4562-b3fc-2c963f66afa6 returned 404",
		"trace 4bf92f3577b34da6a3ce929d0e0e4736 commit 5f37f7ec6a1b2c3d4e5f60718293a4b5c6d7e8f9",
		"The application hit an unexpected error while rendering this page.",
		"http://localhost:5173/src/features/chat/Chat.tsx?t=1700000000000:12:5",
		"example.com/home/page",
		"Applying migration '20260913134250_AddBenchmarkP2Discrimination' from migration 'AddBenchmarkP2Discrimination'",
		"at 2026-10-02T09:16:29.1234567Z and 20261002T091629Z, backup node-chat-20261002T091629-2026100209162912.sqlite",
		"only three digits: AbCdEfGhIjKlMnOp123QrStUv",
		"uppercase hex 4BF92F3577B34DA6A3CE929D0E0E4736",
	])("leaves %s untouched", (input) => {
		expect(redactString(input)).toBe(input);
	});
});
