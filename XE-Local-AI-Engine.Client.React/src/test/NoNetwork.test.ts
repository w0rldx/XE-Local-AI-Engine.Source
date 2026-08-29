// @vitest-environment jsdom

import axios from "axios";
import { describe, expect, it } from "vitest";

import http from "node:http";
import https from "node:https";

// Negative control for src/test/NoNetwork.ts. Removing MSW from the global `setupFiles` only stays safe while
// the guard that replaced it actually refuses an undeclared request on EVERY transport MSW covered — and a
// green suite cannot show that, because the failure being guarded against is a request that quietly succeeds
// against the real network. So this file asserts the refusals directly.
//
// It deliberately does NOT call `setupMswServer()`: it runs under the plain global setup, exactly like the
// ~324 files that no longer pay for MSW.

const guardError = /unexpected network call in test/;
const undeclared = "/api/local/v1/undeclared-route";

describe("no-network guard", () => {
	it("rejects an undeclared fetch and names the URL", async () => {
		await expect(fetch(undeclared)).rejects.toThrow(guardError);
		await expect(fetch(undeclared)).rejects.toThrow(undeclared);
	});

	it("rejects an undeclared axios request, which jsdom routes through the XHR adapter", async () => {
		await expect(axios.get(undeclared)).rejects.toThrow(guardError);
	});

	it("refuses a bare XMLHttpRequest at open()", () => {
		expect(() => new XMLHttpRequest().open("GET", undeclared)).toThrow(guardError);
	});

	it("refuses node http/https requests, which no fetch-only guard would catch", () => {
		expect(() => http.request("http://127.0.0.1:1/undeclared")).toThrow(guardError);
		expect(() => http.get("http://127.0.0.1:1/undeclared")).toThrow(guardError);
		expect(() => https.request("https://127.0.0.1:1/undeclared")).toThrow(guardError);
		expect(() => https.get("https://127.0.0.1:1/undeclared")).toThrow(guardError);
	});
});
