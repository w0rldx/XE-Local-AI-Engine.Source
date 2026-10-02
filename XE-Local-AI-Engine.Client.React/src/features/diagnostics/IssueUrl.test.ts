import { describe, expect, it } from "vitest";

import { SCHEMA_VERSION, type Snapshot } from "@/core/diagnostics/Diagnostics";
import { browserFamily, buildIssueUrl, describeHardware, type IssueNodeInfo, mapIssueOs } from "@/features/diagnostics/IssueUrl";

const GIB = 1024 ** 3;
const chromeUa =
	"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

const nodeInfo: IssueNodeInfo = {
	version: "1.0.0-rc.3+5f37f7ec6",
	repositoryUrl: "https://github.com/w0rldx/XE-Local-AI-Engine.Source/",
	osDescription: "Microsoft Windows 10.0.26100",
	cpuModel: "AMD Ryzen 9 7950X",
	cpuCores: 16,
	totalRamBytes: 64 * GIB,
	gpus: [{ name: "NVIDIA GeForce RTX 5090", totalBytes: 32 * GIB }],
};

const snapshot: Snapshot = {
	id: "s1",
	createdAt: 1,
	schemaVersion: SCHEMA_VERSION,
	kind: "error",
	error: { message: `Cannot read properties of undefined & more ${"x".repeat(100)}`, source: "boundary" },
	breadcrumbs: [],
	network: [],
	env: { route: "/", appVersion: "1", userAgent: "t", viewport: { width: 1, height: 1 }, locale: "en" },
};

function params(url: string): URLSearchParams {
	return new URL(url).searchParams;
}

describe("mapIssueOs", () => {
	it.each([
		["Microsoft Windows 10.0.26100", "Windows 11"],
		["Microsoft Windows 10.0.22000", "Windows 11"],
		["Microsoft Windows 10.0.19045", "Windows 10"],
		["Ubuntu 24.04.1 LTS", "Linux"],
		["Linux 6.18.33.2-microsoft-standard-WSL2 #1 SMP", "Linux"],
		["Darwin 24.0.0 Darwin Kernel Version 24.0.0", "Other"],
		["", "Other"],
	])("maps %s to %s", (description, expected) => {
		expect(mapIssueOs(description)).toBe(expected);
	});
});

describe("browserFamily", () => {
	it.each([
		[chromeUa, "Chrome 129"],
		[`${chromeUa} Edg/128.0.0.0`, "Edge 128"],
		["Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0", "Firefox 130"],
		["Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15", "Safari 17"],
		["curl/8.0", undefined],
	])("reads %s", (userAgent, expected) => {
		expect(browserFamily(userAgent)).toBe(expected);
	});
});

describe("describeHardware", () => {
	it("joins CPU, GPU with VRAM and RAM", () => {
		expect(describeHardware(nodeInfo)).toBe(
			"CPU: AMD Ryzen 9 7950X (16 cores) / GPU: NVIDIA GeForce RTX 5090 (32 GB) / RAM: 64 GB",
		);
	});

	it("leaves out what the node could not read", () => {
		expect(describeHardware({ ...nodeInfo, cpuModel: null, cpuCores: null, totalRamBytes: 16 * GIB, gpus: null })).toBe(
			"RAM: 16 GB",
		);
	});
});

describe("buildIssueUrl", () => {
	it("prefills only template ids, with the snapshot error as the title", () => {
		const url = buildIssueUrl(nodeInfo, snapshot, chromeUa) as string;

		expect(url.startsWith("https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new?template=bug_report.yml&")).toBe(true);
		const query = params(url);
		expect([...query.keys()]).toEqual(["template", "title", "version", "os", "browser", "hardware"]);
		expect(query.get("title")).toBe(snapshot.error?.message.slice(0, 80));
		expect(query.get("version")).toBe("1.0.0-rc.3+5f37f7ec6");
		expect(query.get("os")).toBe("Windows 11");
		expect(query.get("browser")).toBe("Chrome 129");
		expect(query.get("hardware")).toBe(describeHardware(nodeInfo));
	});

	it("percent-encodes reserved characters instead of form-encoding them", () => {
		const url = buildIssueUrl(nodeInfo, snapshot, chromeUa) as string;

		expect(url).toContain("version=1.0.0-rc.3%2B5f37f7ec6");
		expect(url).toContain("undefined%20%26%20more");
		expect(url).not.toContain("+");
	});

	it("falls back to a generic title and skips unknown fields", () => {
		const query = params(
			buildIssueUrl({ ...nodeInfo, cpuModel: null, gpus: [], totalRamBytes: null }, undefined, "curl/8") as string,
		);

		expect(query.get("title")).toBe("Problem report");
		expect(query.has("browser")).toBe(false);
		expect(query.has("hardware")).toBe(false);
	});

	it.each([null, "", "http://github.com/x/y", "javascript:alert(1)"])(
		"returns undefined for repository url %s",
		(repositoryUrl) => {
			expect(buildIssueUrl({ ...nodeInfo, repositoryUrl }, snapshot, chromeUa)).toBeUndefined();
		},
	);
});
