import { describe, expect, it } from "vitest";

import { formatRuntimeBoolean, formatRuntimeBytes, formatRuntimeLogLine, formatRuntimeText, formatRuntimeTimestamp, getComponentHealthColor, getRuntimeStatusColor, manifestSummary, runtimeContainerActionLabel, sortRuntimeComponents } from "@/features/runtime-manager/models/RuntimeManagerModel";
import type { RuntimeComponentStatusDto, RuntimeManifestDto } from "@/features/runtime-manager/api/RuntimeManagerApi";

describe("RuntimeManagerModel", () => {
	it("formats status values for display", () => {
		expect(formatRuntimeBoolean(true)).toBe("Yes");
		expect(formatRuntimeText(" ")).toBe("—");
		expect(formatRuntimeBytes(1_073_741_824)).toBe("1.0 GB");
		expect(formatRuntimeTimestamp("1970-01-01T00:00:00Z")).toBe("Not reported");
		expect(getRuntimeStatusColor("Running")).toBe("green");
		expect(getComponentHealthColor("Unhealthy")).toBe("red");
	});

	it("sorts components by name", () => {
		const components: RuntimeComponentStatusDto[] = [
			createComponent("web"),
			createComponent("ollama"),
		];

		expect(sortRuntimeComponents(components).map((component) => component.name)).toEqual(["ollama", "web"]);
	});

	it("summarizes manifest availability", () => {
		expect(manifestSummary(createManifest(true))).toBe("managed runtime · 1 container");
		expect(manifestSummary(createManifest(false))).toBe("Runtime manifest unavailable");
	});

	it("labels container actions", () => {
		expect(runtimeContainerActionLabel("start")).toBe("Start");
		expect(runtimeContainerActionLabel("stop")).toBe("Stop");
		expect(runtimeContainerActionLabel("restart")).toBe("Restart");
	});

	it("formats runtime log lines", () => {
		expect(formatRuntimeLogLine({ containerName: "ollama", stream: "stdout", line: "ready", observedAt: "2026-05-24T12:00:00Z" })).toContain("ollama/stdout: ready");
	});
});

function createComponent(name: string): RuntimeComponentStatusDto {
	return {
		name,
		desiredState: "Running",
		health: "Healthy",
		imageReference: "image@sha256:test",
		digestVerified: true,
		observedAt: "2026-05-24T12:00:00Z",
		diagnostics: [],
	};
}

function createManifest(available: boolean): RuntimeManifestDto {
	return {
		available,
		schemaVersion: available ? 1 : null,
		runtimeMode: "managed",
		bootstrapModel: "qwen3:0.6b",
		defaultChatModel: "qwen3:8b",
		maxRuntimeDiskGb: 128,
		stopDrainTimeoutSeconds: 30,
		containers: available
			? [
				{
					name: "ollama",
					image: "ollama/ollama:0.11.10",
					network: "xe-engine-net",
					environment: [],
					volumes: [],
				},
			]
			: [],
		diagnostics: available ? [] : ["manifest-not-configured"],
	};
}
