import type { TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import {
	externalUsageConnectionId,
	providerColor,
	providerLabel,
} from "@/features/usage-dashboard/models/UsageProviderPresentation";

// The usage components pass i18next's own `t`. Here it is stood in for by the fallback-returning shape the call sites
// use — `t(key, fallback)` — so these assertions are about the formatting, not about the translation catalogue.
const t = ((_key: string, fallback: string) => fallback) as unknown as TFunction;

describe("externalUsageConnectionId", () => {
	it("recovers the connection id from an external usage-provider string", () => {
		expect(externalUsageConnectionId("external:unsloth-box")).toBe("unsloth-box");
	});

	it("returns null for every other provider", () => {
		expect(externalUsageConnectionId("local")).toBeNull();
		expect(externalUsageConnectionId("azure")).toBeNull();
		expect(externalUsageConnectionId("externalish")).toBeNull();
	});
});

describe("providerLabel", () => {
	it("keeps the existing labels for the built-in providers", () => {
		expect(providerLabel("local", t)).toBe("Local (llama.cpp)");
		expect(providerLabel("codex", t)).toBe("Codex");
		expect(providerLabel("unknown", t)).toBe("Unknown");
	});

	// The ledger records `external:{connectionId}` because it holds ONE string per run and a display name can change.
	// The name is resolved at display time from the configuration the page already reads.
	it("renders an external connection by name rather than as the raw provider string", () => {
		const names = new Map([["unsloth-box", "Unsloth box"]]);

		expect(providerLabel("external:unsloth-box", t, names)).toBe("External · Unsloth box");
	});

	it("falls back to the bare label for a connection that has since been deleted, or with no lookup at all", () => {
		expect(providerLabel("external:gone", t, new Map())).toBe("External");
		expect(providerLabel("external:gone", t)).toBe("External");
	});

	it("ignores a blank stored name rather than rendering a dangling separator", () => {
		expect(providerLabel("external:gateway", t, new Map([["gateway", "   "]]))).toBe("External");
	});
});

describe("providerColor", () => {
	it("gives every external connection the same colour, distinct from the unknown fallback", () => {
		expect(providerColor("external:unsloth-box")).toBe(providerColor("external:gateway"));
		expect(providerColor("external:unsloth-box")).not.toBe(providerColor("unknown"));
	});
});
