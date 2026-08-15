import { describe, expect, it } from "vitest";

import type { TrainingRunEvent } from "@/features/training/models/TrainingModels";
import {
	applyRunEvent,
	emptyTrainingRunProgress,
	isRunActive,
	runPercent,
	trainingRunEventSchema,
} from "@/features/training/models/TrainingModels";

function event(kind: TrainingRunEvent["kind"], payload: TrainingRunEvent["payload"], sequence = 1): TrainingRunEvent {
	return { runId: "run-1", sequence, kind, payload };
}

describe("training run progress folding", () => {
	it("keeps the last reported value for every field a later event omits", () => {
		const afterProgress = applyRunEvent(emptyTrainingRunProgress, event("Progress", { step: 7, totalSteps: 40, loss: 1.25 }));
		// A later tick that only carries a step must not blank the loss the previous one reported.
		const afterStep = applyRunEvent(afterProgress, event("Progress", { step: 8 }, 2));

		expect(afterStep.step).toBe(8);
		expect(afterStep.totalSteps).toBe(40);
		expect(afterStep.loss).toBe(1.25);
	});

	it("tracks the trainer's phase separately from the run's status", () => {
		const withPhase = applyRunEvent(emptyTrainingRunProgress, event("Phase", { phase: "training" }));
		const withStatus = applyRunEvent(withPhase, event("State", { state: "Training" }, 2));

		expect(withStatus.phase).toBe("training");
		expect(withStatus.status).toBe("Training");
	});

	it("carries an error message through so a failed run can say why", () => {
		const failed = applyRunEvent(emptyTrainingRunProgress, event("Error", { message: "CUDA out of memory" }));

		expect(failed.message).toBe("CUDA out of memory");
	});
});

describe("training run event schema", () => {
	it("accepts an event whose optional payload fields are absent", () => {
		expect(trainingRunEventSchema.safeParse({ runId: "run-1", sequence: 1, kind: "Progress", payload: {} }).success).toBe(true);
	});

	it("rejects an unknown event kind rather than folding it as progress", () => {
		expect(trainingRunEventSchema.safeParse({ runId: "run-1", sequence: 1, kind: "Nope", payload: {} }).success).toBe(false);
	});
});

describe("run status helpers", () => {
	it("treats only the three terminal statuses as finished", () => {
		expect(isRunActive("Queued")).toBe(true);
		expect(isRunActive("Preparing")).toBe(true);
		expect(isRunActive("Training")).toBe(true);
		expect(isRunActive("Succeeded")).toBe(false);
		expect(isRunActive("Failed")).toBe(false);
		expect(isRunActive("Cancelled")).toBe(false);
	});

	it("reports no percentage until the trainer knows its total", () => {
		// A bar that guesses is worse than one that admits it does not know yet.
		expect(runPercent(3, 0)).toBeNull();
		expect(runPercent(0, 40)).toBe(0);
		expect(runPercent(20, 40)).toBe(50);
		expect(runPercent(99, 40)).toBe(100);
	});
});
