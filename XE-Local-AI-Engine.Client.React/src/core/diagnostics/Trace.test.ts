import { describe, expect, it } from "vitest";

import { ensureConfigTrace, extractTraceId, generateTraceparent, getTraceId, rememberTraceId } from "@/core/diagnostics/Trace";

const W3C_TRACEPARENT = /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/;

describe("generateTraceparent", () => {
	it("emits a valid W3C traceparent header and matching trace id", () => {
		const { header, traceId } = generateTraceparent();
		expect(header).toMatch(W3C_TRACEPARENT);
		expect(traceId).toMatch(/^[0-9a-f]{32}$/);
		expect(header).toContain(traceId);
	});

	it("produces unique ids across calls", () => {
		expect(generateTraceparent().traceId).not.toBe(generateTraceparent().traceId);
	});
});

describe("config → traceId WeakMap pairing", () => {
	it("stashes and reads back a trace id by config identity", () => {
		const config = {};
		rememberTraceId(config, "0af7651916cd43dd8448eb211c80319c");
		expect(getTraceId(config)).toBe("0af7651916cd43dd8448eb211c80319c");
		expect(getTraceId({})).toBeUndefined();
	});

	it("reuses the original trace id on a 401-style retry (same config object)", () => {
		const config = {};
		const first = ensureConfigTrace(config);
		const retry = ensureConfigTrace(config);

		// Same config → same trace id (one logical request, one trace), even though the span differs.
		expect(retry.traceId).toBe(first.traceId);
		expect(retry.header).toMatch(W3C_TRACEPARENT);
		expect(getTraceId(config)).toBe(first.traceId);
	});
});

describe("extractTraceId", () => {
	it("parses the trace id from a traceparent/traceresponse header", () => {
		expect(extractTraceId("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")).toBe("0af7651916cd43dd8448eb211c80319c");
	});

	it("accepts a bare 32-hex X-Trace-Id value", () => {
		expect(extractTraceId("0AF7651916CD43DD8448EB211C80319C")).toBe("0af7651916cd43dd8448eb211c80319c");
	});

	it("returns undefined for empty/invalid input", () => {
		expect(extractTraceId(undefined)).toBeUndefined();
		expect(extractTraceId("not-a-trace")).toBeUndefined();
	});
});
