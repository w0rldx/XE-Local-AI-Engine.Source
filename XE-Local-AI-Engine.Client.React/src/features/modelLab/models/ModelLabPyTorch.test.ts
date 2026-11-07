import i18next from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import {
	type CheckpointPreflight,
	checkpointShapeDiffers,
	type PyTorchRefusal,
	pyTorchRefusal,
	pyTorchRefusalMessage,
} from "@/features/modelLab/models/ModelLabPyTorch";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { i18nReady } from "@/i18n";

// A Record, so a reason added to the generated union fails to compile here until it is listed.
const reasons = Object.keys({
	Busy: true,
	CheckpointNotFound: true,
	CheckpointNotReady: true,
	ConfigMissing: true,
	ConfigTooLarge: true,
	ConfigUnreadable: true,
	IndexFailed: true,
	NoBlocks: true,
	RemoteCode: true,
	RepoGated: true,
	RepoUnreachable: true,
	ResultTooLarge: true,
	RuntimeUnavailable: true,
	SourceChanged: true,
	SourceNotInIndex: true,
	SourceTooLarge: true,
	UnsupportedModelType: true,
	UnsupportedPlatform: true,
} satisfies Record<PyTorchRefusal, true>) as PyTorchRefusal[];

describe("pyTorchRefusalMessage", () => {
	beforeAll(async () => {
		await i18nReady;
		await loadModelLabStrings("en");
		await loadModelLabStrings("de");
	});

	it("gives every refusal its own message, in English and in German", () => {
		const english = reasons.map((reason) => pyTorchRefusalMessage(i18next.getFixedT("en"), reason));
		const german = reasons.map((reason) => pyTorchRefusalMessage(i18next.getFixedT("de"), reason));

		expect(new Set(english).size).toBe(reasons.length);
		expect(new Set(german).size).toBe(reasons.length);
		expect(german.filter((message, index) => message === english[index])).toEqual([]);
		expect(pyTorchRefusalMessage(i18next.getFixedT("en"), "UnsupportedPlatform")).toBe("The PyTorch index works on Linux only.");
		expect(pyTorchRefusalMessage(i18next.getFixedT("en"), "RemoteCode")).toContain("trust_remote_code");
	});

	it("reads the reason only off a 409", () => {
		const body = { reason: "Busy", message: "busy" } as unknown as ProblemDetails;

		expect(pyTorchRefusal(new ApiError(409, body))).toBe("Busy");
		expect(pyTorchRefusal(new ApiError(400, body))).toBeNull();
		expect(pyTorchRefusal(new Error("network"))).toBeNull();
	});
});

describe("checkpointShapeDiffers", () => {
	const shape = { checkpointHiddenSize: 1024, ggufEmbeddingLength: 1024, checkpointBlockCount: 64, steerableBlockCount: 64 };

	it("hides a checkpoint only on a known hidden size or trunk block count of another value", () => {
		expect(checkpointShapeDiffers(shape as CheckpointPreflight)).toBe(false);
		expect(checkpointShapeDiffers({ ...shape, checkpointHiddenSize: 5120 } as CheckpointPreflight)).toBe(true);
		expect(checkpointShapeDiffers({ ...shape, checkpointBlockCount: 24 } as CheckpointPreflight)).toBe(true);
		// Not yet answered, or refused before the shapes were read.
		expect(checkpointShapeDiffers(undefined)).toBe(false);
		expect(
			checkpointShapeDiffers({ ...shape, checkpointHiddenSize: null, checkpointBlockCount: null } as CheckpointPreflight),
		).toBe(false);
	});
});
