import { describe, expect, it } from "vitest";

import { toEjectRunningModelResult, toRunningModel } from "@/features/loaded-models/models/RunningModelsModels";

describe("toRunningModel", () => {
	it("maps the running-model wire shape to the domain view-model", () => {
		expect(
			toRunningModel({
				modelName: "qwen3:8b",
				role: "chat",
				isResponsive: true,
				detail: "ok",
				detailCode: "responsive",
				isBusy: true,
				lastUsedUtc: "2026-09-29T10:00:00Z",
				isTransient: true,
				effectiveContextTokens: 65536,
				expertsOffloaded: true,
			}),
		).toEqual({
			modelName: "qwen3:8b",
			role: "chat",
			isResponsive: true,
			detail: "ok",
			detailCode: "responsive",
			isBusy: true,
			lastUsedUtc: "2026-09-29T10:00:00Z",
			isTransient: true,
			effectiveContextTokens: 65536,
			expertsOffloaded: true,
		});
	});

	it("reads an absent window as unknown and an absent placement as not offloaded", () => {
		const model = toRunningModel({
			modelName: "m",
			role: "chat",
			isResponsive: true,
			detail: "",
			detailCode: "responsive",
			isBusy: false,
			lastUsedUtc: null,
			isTransient: false,
		});
		expect(model.effectiveContextTokens).toBeNull();
		expect(model.expertsOffloaded).toBe(false);
	});

	it("keeps a known detail code and drops an unknown one", () => {
		const wire = {
			modelName: "m",
			role: "chat",
			isResponsive: false,
			detail: "Process has exited.",
			isBusy: false,
			lastUsedUtc: null,
			isTransient: false,
		};
		expect(toRunningModel({ ...wire, detailCode: "exited" }).detailCode).toBe("exited");
		expect(toRunningModel({ ...wire, detailCode: "surprise" }).detailCode).toBeNull();
	});
});

describe("toEjectRunningModelResult eject outcomes", () => {
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
