import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse } from "@/core/api/generated";
import { toGgufRepositoryDetail, toGgufTestedModel } from "@/features/models/models/GgufMappers";
import {
	type GgufFitVerdict,
	type GgufRepositoryFile,
	type GgufTestedModel,
	preferredGgufFileName,
	recommendedGgufFileName,
	sortTestedModelsByFit,
} from "@/features/models/models/GgufModels";

describe("toGgufRepositoryDetail file mapping", () => {
	it("maps the new quality/fit/recommended fields from the wire shape", () => {
		const response: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse = {
			repoId: "owner/repo",
			hasProjector: false,
			files: [
				{
					fileName: "model-Q5_K_M.gguf",
					quant: "Q5_K_M",
					isDynamic: false,
					isDraft: false,
					sizeBytes: 5_000_000_000,
					qualityTier: "SweetSpot",
					fitVerdict: "Fits",
					isRecommended: true,
				},
			],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({
			qualityTier: "SweetSpot",
			fitVerdict: "Fits",
			isRecommended: true,
		});
	});

	it("coalesces omitted quality/fit/recommended fields to neutral defaults", () => {
		const response: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse = {
			repoId: "owner/repo",
			hasProjector: false,
			files: [
				{
					fileName: "model-Q4_K_M.gguf",
					quant: "Q4_K_M",
					isDynamic: false,
					isDraft: false,
					sizeBytes: 4_000_000_000,
					qualityTier: "Balanced",
					fitVerdict: "Unknown",
					isRecommended: false,
				},
			],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({
			qualityTier: "Balanced",
			fitVerdict: "Unknown",
			isRecommended: false,
		});
	});

	it("maps a file verdict this client does not know to Unknown", () => {
		const detail = toGgufRepositoryDetail({
			repoId: "owner/repo",
			hasProjector: false,
			files: [
				{
					fileName: "model-Q4_K_M.gguf",
					quant: "Q4_K_M",
					isDynamic: false,
					isDraft: false,
					sizeBytes: 4_000_000_000,
					qualityTier: "Balanced",
					fitVerdict: "Marginal",
					isRecommended: false,
				},
			],
		});

		expect(detail.files[0]?.fitVerdict).toBe("Unknown");
	});
});

describe("vision-projector fields", () => {
	it("maps the projector flag and its size through", () => {
		const detail = toGgufRepositoryDetail({
			repoId: "unsloth/gemma-3-12b-it-GGUF",
			hasProjector: true,
			projectorSizeBytes: 850_000_000,
			files: [],
		});

		expect(detail).toMatchObject({ hasProjector: true, projectorSizeBytes: 850_000_000 });
	});

	// An older backend sends neither field. Defaulting to "no projector" means the dialog offers no weights-only
	// choice and sends nothing, so the server default applies — today's behaviour, rather than a checkbox that lies.
	it("treats omitted projector fields as no projector", () => {
		const detail = toGgufRepositoryDetail({
			repoId: "owner/repo",
			files: [],
		} as unknown as XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse);

		expect(detail).toMatchObject({ hasProjector: false, projectorSizeBytes: null });
	});

	it("reports a projector whose size the backend did not send", () => {
		const detail = toGgufRepositoryDetail({
			repoId: "owner/repo",
			hasProjector: true,
			projectorSizeBytes: null,
			files: [],
		});

		expect(detail).toMatchObject({ hasProjector: true, projectorSizeBytes: null });
	});
});

describe("draft-model rows", () => {
	it("carries the backend draft flag and its marked quant label through the mapper", () => {
		const response: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse = {
			repoId: "unsloth/gemma-4-12b-it-GGUF",
			hasProjector: false,
			files: [
				{
					fileName: "MTP/mtp-gemma-4-12b-it-Q8_0.gguf",
					quant: "MTP-Q8_0",
					isDynamic: false,
					isDraft: true,
					sizeBytes: 400_000_000,
					qualityTier: "Balanced",
					fitVerdict: "Fits",
					isRecommended: false,
				},
			],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({ isDraft: true, quant: "MTP-Q8_0" });
	});

	it("treats an omitted draft flag as a base quant so an older backend hides nothing", () => {
		const detail = toGgufRepositoryDetail({
			repoId: "owner/repo",
			files: [{ fileName: "model-Q4_K_M.gguf", quant: "Q4_K_M" }],
		} as XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse);

		expect(detail.files[0]?.isDraft).toBe(false);
	});
});

describe("recommendedGgufFileName", () => {
	const file = (fileName: string, isRecommended: boolean, isDraft = false): GgufRepositoryFile => ({
		fileName,
		quant: fileName,
		isDynamic: false,
		isDraft,
		sizeBytes: 1,
		qualityTier: "Balanced",
		fitVerdict: "Unknown",
		isRecommended,
	});

	it("returns the recommended file's name when one is flagged", () => {
		const files = [file("a.gguf", false), file("b.gguf", true), file("c.gguf", false)];

		expect(recommendedGgufFileName(files)).toBe("b.gguf");
	});

	it("falls back to the first file when none is recommended", () => {
		const files = [file("a.gguf", false), file("b.gguf", false)];

		expect(recommendedGgufFileName(files)).toBe("a.gguf");
	});

	it("never falls back to a speculative-decoding draft", () => {
		// The live gemma-4-12b list: MTP drafters are the smallest files, so a plain files[0] fallback selected
		// a 0.4 GB drafter by default. Only an explicit click may ever select one.
		const files = [file("MTP/mtp-gemma-4-12b-it-Q8_0.gguf", false, true), file("gemma-4-12b-it-Q8_0.gguf", false)];

		expect(recommendedGgufFileName(files)).toBe("gemma-4-12b-it-Q8_0.gguf");
	});

	it("returns null when the repository lists nothing but drafts", () => {
		expect(recommendedGgufFileName([file("MTP/mtp-gemma-4-12b-it-Q8_0.gguf", false, true)])).toBeNull();
	});

	it("returns null for an empty list", () => {
		expect(recommendedGgufFileName([])).toBeNull();
	});
});

describe("toGgufTestedModel", () => {
	it("maps a tested catalog model with its tested quant, size and fit, and coalesces an absent note to null", () => {
		const model = toGgufTestedModel({
			id: "granite-4.1-3b",
			displayName: "Granite 4.1 3B",
			publisher: "IBM",
			ggufRepo: "unsloth/granite-4.1-3b-GGUF",
			license: "apache-2.0",
			totalParamsB: 3.4,
			testedQuant: "Q4_K_M",
			testedSizeBytes: 2_099_502_400,
			fitVerdict: "Tight",
		});

		expect(model).toEqual({
			id: "granite-4.1-3b",
			displayName: "Granite 4.1 3B",
			ggufRepo: "unsloth/granite-4.1-3b-GGUF",
			license: "apache-2.0",
			totalParamsB: 3.4,
			notes: null,
			testedQuant: "Q4_K_M",
			testedSizeBytes: 2_099_502_400,
			fitVerdict: "Tight",
		});
	});

	it("maps a verdict this client does not know to Unknown", () => {
		const model = toGgufTestedModel({
			id: "granite-4.1-3b",
			displayName: "Granite 4.1 3B",
			publisher: "IBM",
			ggufRepo: "unsloth/granite-4.1-3b-GGUF",
			license: "apache-2.0",
			totalParamsB: 3.4,
			testedQuant: "Q4_K_M",
			testedSizeBytes: 2_099_502_400,
			fitVerdict: "Marginal",
		});

		expect(model.fitVerdict).toBe("Unknown");
	});
});

describe("preferredGgufFileName", () => {
	const file = (quant: string, fitVerdict: GgufRepositoryFile["fitVerdict"], isRecommended = false): GgufRepositoryFile => ({
		fileName: `model-${quant}.gguf`,
		quant,
		isDynamic: quant.startsWith("UD-"),
		isDraft: false,
		sizeBytes: 1,
		qualityTier: "Balanced",
		fitVerdict,
		isRecommended,
	});

	it("selects the tested quant case-insensitively over the recommended file", () => {
		const files = [file("UD-Q4_K_M", "Fits"), file("Q8_0", "Fits", true)];

		expect(preferredGgufFileName(files, "ud-q4_k_m")).toBe("model-UD-Q4_K_M.gguf");
	});

	it("keeps the recommended file when the tested quant won't fit", () => {
		const files = [file("Q4_K_M", "WontFit"), file("Q2_K", "Tight", true)];

		expect(preferredGgufFileName(files, "Q4_K_M")).toBe("model-Q2_K.gguf");
	});

	it("keeps the recommended file when the repo does not offer the tested quant or none is given", () => {
		const files = [file("Q5_K_M", "Fits", true)];

		expect(preferredGgufFileName(files, "Q4_K_M")).toBe("model-Q5_K_M.gguf");
		expect(preferredGgufFileName(files, undefined)).toBe("model-Q5_K_M.gguf");
	});
});

describe("sortTestedModelsByFit", () => {
	const tested = (id: string, totalParamsB: number, fitVerdict: GgufFitVerdict): GgufTestedModel => ({
		id,
		displayName: id,
		ggufRepo: `owner/${id}`,
		license: "apache-2.0",
		totalParamsB,
		notes: null,
		testedQuant: "Q4_K_M",
		testedSizeBytes: 1,
		fitVerdict,
	});
	const ids = (models: readonly GgufTestedModel[]): string[] => models.map((model) => model.id);

	it("lists Fits then Tight largest first, then WontFit smallest first", () => {
		const models = [
			tested("wont-big", 70, "WontFit"),
			tested("fits-small", 3, "Fits"),
			tested("tight-small", 8, "Tight"),
			tested("wont-small", 32, "WontFit"),
			tested("fits-big", 14, "Fits"),
			tested("tight-big", 27, "Tight"),
		];

		expect(ids(sortTestedModelsByFit(models))).toEqual([
			"fits-big",
			"fits-small",
			"tight-big",
			"tight-small",
			"wont-small",
			"wont-big",
		]);
	});

	it("keeps the given order while any verdict is Unknown", () => {
		const models = [tested("b", 3, "WontFit"), tested("a", 14, "Unknown"), tested("c", 8, "Fits")];

		expect(ids(sortTestedModelsByFit(models))).toEqual(["b", "a", "c"]);
	});

	it("breaks a size tie by id", () => {
		const models = [
			tested("qwen", 8, "Fits"),
			tested("granite", 8, "Fits"),
			tested("z", 8, "WontFit"),
			tested("y", 8, "WontFit"),
		];

		expect(ids(sortTestedModelsByFit(models))).toEqual(["granite", "qwen", "y", "z"]);
	});

	it("does not mutate its input", () => {
		const models = [tested("small", 3, "Fits"), tested("big", 14, "Fits")];

		sortTestedModelsByFit(models);

		expect(ids(models)).toEqual(["small", "big"]);
	});
});
