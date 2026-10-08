import { describe, expect, it } from "vitest";

import { type CustomHeaderDraft, toHeaderRequests, validateHeaders } from "@/core/http-headers/CustomHeaders";

function header(overrides: Partial<CustomHeaderDraft> = {}): CustomHeaderDraft {
	return { name: "X-Custom", value: "v", isSecret: false, hasStoredSecret: false, ...overrides };
}

function messageFor(headers: CustomHeaderDraft[]): string | undefined {
	return validateHeaders(headers)?.message;
}

describe("validateHeaders", () => {
	it("accepts a valid set of custom headers", () => {
		expect(
			validateHeaders([header({ name: "Ocp-Apim-Subscription-Key", value: "abc" }), header({ name: "X-Route", value: "east" })]),
		).toBeUndefined();
	});

	it("ignores fully blank rows but rejects a blank name that carries a value", () => {
		expect(validateHeaders([header({ name: "", value: "" })])).toBeUndefined();
		expect(validateHeaders([header({ name: "  ", value: "v" })])).toEqual({
			message: "Enter a header name for every row that has a value.",
			index: 0,
		});
	});

	it("rejects duplicate names case-insensitively and names the second row", () => {
		const error = validateHeaders([header({ name: "X-Api" }), header({ name: "x-api" })]);
		expect(error?.message).toContain("duplicated");
		expect(error?.index).toBe(1);
	});

	it("rejects every reserved name in any case", () => {
		const reserved = [
			"Authorization",
			"api-key",
			"HOST",
			"Content-Type",
			"content-length",
			"Content-Encoding",
			"COOKIE",
			"Proxy-Authorization",
			"Transfer-Encoding",
			"connection",
			"Expect",
			"Allow",
			"content-disposition",
			"Content-Language",
			"CONTENT-LOCATION",
			"Content-MD5",
			"Content-Range",
			"expires",
			"Last-Modified",
		];
		for (const name of reserved) {
			expect(messageFor([header({ name })])).toContain("reserved");
		}
	});

	it("rejects non-RFC-token characters in the name", () => {
		expect(messageFor([header({ name: "Bad Header" })])).toContain("not allowed");
		expect(messageFor([header({ name: "Bad:Header" })])).toContain("not allowed");
	});

	it("rejects control characters in the value but allows HTAB", () => {
		expect(messageFor([header({ value: "a\r\nb" })])).toContain("control character");
		expect(messageFor([header({ value: "a\u0000b" })])).toContain("control character");
		expect(messageFor([header({ value: "a\u007fb" })])).toContain("control character");
		expect(validateHeaders([header({ value: "a\tb" })])).toBeUndefined();
	});

	it("rejects any non-ASCII character in the value with its own message", () => {
		for (const value of ["café", "a b", "€", "\u{1F600}", "\u0080"]) {
			expect(messageFor([header({ value })])).toBe(
				'Value for header "X-Custom" contains a non-ASCII character; header values must be plain ASCII.',
			);
		}
		expect(validateHeaders([header({ value: "~ascii only!" })])).toBeUndefined();
	});

	it("enforces the header count and length caps", () => {
		const many = Array.from({ length: 33 }, (_, i) => header({ name: `X-${i}` }));
		expect(validateHeaders(many)).toEqual({ message: expect.stringContaining("at most 32") });
		expect(validateHeaders(many.slice(0, 32))).toBeUndefined();
		expect(messageFor([header({ name: "a".repeat(129) })])).toContain("too long");
		expect(validateHeaders([header({ name: "a".repeat(128) })])).toBeUndefined();
		expect(messageFor([header({ value: "a".repeat(4097) })])).toContain("too long");
	});

	it("blocks secret resurrection when Secret is turned off on a stored-secret row left blank", () => {
		expect(messageFor([header({ name: "X-Secret", value: "", isSecret: false, hasStoredSecret: true })])).toContain(
			"stored secret is not reused",
		);
	});

	it("requires a value for a secret row with nothing stored", () => {
		expect(messageFor([header({ name: "X-Secret", value: "", isSecret: true, hasStoredSecret: false })])).toContain(
			'Enter a value for the secret header "X-Secret"',
		);
	});

	it("keeps a stored secret when the row is secret and left blank", () => {
		expect(validateHeaders([header({ name: "X-Secret", value: "", isSecret: true, hasStoredSecret: true })])).toBeUndefined();
	});
});

describe("toHeaderRequests", () => {
	it("drops blank-name rows, trims names, and omits a blank secret value so the stored one is kept", () => {
		expect(
			toHeaderRequests([
				header({ name: " X-Plain ", value: "east" }),
				header({ name: "", value: "" }),
				header({ name: "X-Kept", value: " ", isSecret: true, hasStoredSecret: true }),
				header({ name: "X-New", value: "s3cret", isSecret: true }),
			]),
		).toEqual([
			{ name: "X-Plain", isSecret: false, value: "east" },
			{ name: "X-Kept", isSecret: true },
			{ name: "X-New", isSecret: true, value: "s3cret" },
		]);
	});
});
