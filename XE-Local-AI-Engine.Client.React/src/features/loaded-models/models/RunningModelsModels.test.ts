import { describe, expect, it } from "vitest";

import { toEjectRunningModelResult, toRunningModel } from "@/features/loaded-models/models/RunningModelsModels";

describe("toRunningModel", () => {
	it("maps the running-model wire shape to the domain view-model", () => {
		expect(toRunningModel({ modelName: "qwen3:8b", role: "chat", isResponsive: true, detail: "ok" })).toEqual({
			modelName: "qwen3:8b",
			role: "chat",
			isResponsive: true,
			detail: "ok",
		});
	});
});

describe("toEjectRunningModelResult (AUD4-20 eject outcomes)", () => {
	it("maps each backend outcome to the domain union", () => {
		expect(toEjectRunningModelResult({ modelName: "m", role: "chat", outcome: "ejected" }).outcome).toBe("ejected");
		expect(toEjectRunningModelResult({ modelName: "m", role: "chat", outcome: "timed_out_still_busy" }).outcome).toBe(
			"timed_out_still_busy",
		);
		expect(toEjectRunningModelResult({ modelName: "m", role: "chat", outcome: "forced" }).outcome).toBe("forced");
		expect(toEjectRunningModelResult({ modelName: "m", role: "chat", outcome: "not_running" }).outcome).toBe("not_running");
	});

	it("carries the model name and role through", () => {
		expect(toEjectRunningModelResult({ modelName: "qwen3:8b", role: "embedding", outcome: "ejected" })).toEqual({
			modelName: "qwen3:8b",
			role: "embedding",
			outcome: "ejected",
		});
	});

	it("degrades an unrecognised or missing outcome to a safe 'ejected' rather than an out-of-union value", () => {
		expect(toEjectRunningModelResult({ modelName: "m", role: "chat", outcome: "surprise" }).outcome).toBe("ejected");
		expect(toEjectRunningModelResult(undefined).outcome).toBe("ejected");
	});
});
