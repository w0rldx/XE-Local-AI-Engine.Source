// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkProjectForm } from "@/features/benchmarks/components/BenchmarkProjectForm";
import type {
	BenchmarkEligibleModel,
	BenchmarkProjectDraft,
	BenchmarkRubric,
} from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRubricPresets } from "@/features/benchmarks/queries/useBenchmarks";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Validation here is deliberately attempt-gated: errors stay hidden until the first submit, so a fresh form is not
// red before the operator has typed anything. The judge requirements are conditional — they only apply once judging is
// enabled — and a FROZEN project keeps its task/agent/context read-only while its judge stays editable, because a
// judge change re-scores the existing runs instead of invalidating them.

const agents = [{ id: "agent-1", name: "Summariser" }];
const models: BenchmarkEligibleModel[] = [
	{
		modelName: "judge.gguf",
		maxContextTokens: 8192,
		effectiveContextTokens: 8192,
		origin: "imported",
		modelContentFingerprint: "v1:judge",
		supportsTools: false,
	},
];

const rubric = (criteria: BenchmarkRubric["criteria"]): BenchmarkRubric => ({ version: 1, criteria });
const defaultRubric = rubric([{ id: "accuracy", title: "Accuracy", description: "Are the facts right?", weight: 50 }]);
const presets: BenchmarkRubricPresets = {
	default: defaultRubric,
	programming: rubric([{ id: "correctness", title: "Correctness", description: "Does the code run?", weight: 60 }]),
	reasoning: rubric([{ id: "steps", title: "Steps", description: "Is the chain sound?", weight: 40 }]),
};

function draft(overrides: Partial<BenchmarkProjectDraft> = {}): BenchmarkProjectDraft {
	return {
		name: "Summarisation",
		coreTask: "Summarise the attached text.",
		contextTokens: 4096,
		agentDefinitionId: "agent-1",
		judgeEnabled: false,
		judgeModelName: null,
		judgeContextTokens: null,
		rubric: null,
		referenceAnswer: null,
		...overrides,
	};
}

const judgingDraft = (overrides: Partial<BenchmarkProjectDraft> = {}): BenchmarkProjectDraft =>
	draft({
		judgeEnabled: true,
		judgeModelName: "judge.gguf",
		judgeContextTokens: 8192,
		rubric: defaultRubric,
		...overrides,
	});

function renderForm(initialValues: BenchmarkProjectDraft, props: Record<string, unknown> = {}) {
	const onSubmit = vi.fn();
	const view = renderWithProviders(
		<BenchmarkProjectForm initialValues={initialValues} agents={agents} models={models} onSubmit={onSubmit} {...props} />,
	);
	return { ...view, onSubmit };
}

/** Presses Save by submitting the form element, which is what the Save button does. */
function save(container: HTMLElement): void {
	const form = container.querySelector("form");
	expect(form).not.toBeNull();
	fireEvent.submit(form as HTMLFormElement);
}

describe("BenchmarkProjectForm", () => {
	afterEach(cleanup);

	it("submits a complete draft unchanged", () => {
		const values = draft();
		const { container, onSubmit } = renderForm(values);

		save(container);

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(values);
	});

	it("hides validation errors until the first submit attempt", () => {
		const { container } = renderForm(draft({ name: "" }));

		expect(screen.queryByText("Name is required.")).toBeNull();

		save(container);

		expect(screen.getByText("Name is required.")).toBeTruthy();
	});

	it.each([
		["a blank name", { name: "   " }, "Name is required."],
		["a blank core task", { coreTask: "   " }, "Core task is required."],
		["a non-positive context", { contextTokens: 0 }, "Context must be positive."],
		["no agent", { agentDefinitionId: "" }, "Select an agent."],
	])("blocks submit on %s", (_case, overrides, message) => {
		const { container, onSubmit } = renderForm(draft(overrides as Partial<BenchmarkProjectDraft>));

		save(container);

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText(message)).toBeTruthy();
	});

	// The judge fields are required only while judging is on — an off judge must not block a save.
	it("requires the judge model and context only once judging is enabled", () => {
		const off = renderForm(draft({ judgeEnabled: false }));
		save(off.container);
		expect(off.onSubmit).toHaveBeenCalledOnce();
		off.unmount();

		const on = renderForm(draft({ judgeEnabled: true }));
		save(on.container);

		expect(on.onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText("Select a judge model.")).toBeTruthy();
		expect(screen.getByText("Judge context must be positive.")).toBeTruthy();
	});

	it("submits a complete judge configuration", () => {
		const values = judgingDraft({ referenceAnswer: "The ideal answer." });
		const { container, onSubmit } = renderForm(values, { presets });

		save(container);

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(values);
	});

	// A null rubric means "the node's default"; once judging is on the operator edits a concrete one.
	it("materialises the default preset as the starting rubric", () => {
		const { container, onSubmit } = renderForm(draft({ judgeEnabled: true, judgeModelName: "judge.gguf", judgeContextTokens: 8192 }), {
			presets,
		});

		expect(screen.getByTestId("benchmark-rubric-editor")).toBeTruthy();
		save(container);

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ rubric: defaultRubric }));
	});

	it("adds and removes rubric criteria within the node's bounds", () => {
		const { container, onSubmit } = renderForm(judgingDraft(), { presets });

		fireEvent.click(screen.getByTestId("benchmark-rubric-add"));
		expect(screen.getByTestId("benchmark-rubric-criterion-1")).toBeTruthy();
		// The single remaining criterion cannot be removed: a rubric with none would be refused by the node.
		fireEvent.click(screen.getByTestId("benchmark-rubric-remove-1"));
		expect(screen.queryByTestId("benchmark-rubric-criterion-1")).toBeNull();
		expect((screen.getByTestId("benchmark-rubric-remove-0") as HTMLButtonElement).disabled).toBe(true);

		save(container);
		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ rubric: defaultRubric }));
	});

	it("replaces the whole rubric with a preset", () => {
		const { container, onSubmit } = renderForm(judgingDraft(), { presets });

		fireEvent.click(screen.getByTestId("benchmark-rubric-preset-programming"));
		save(container);

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ rubric: presets.programming }));
	});

	// A criterion the node would reject must be refused here, on the row that carries it.
	it("blocks submit on an incomplete criterion", () => {
		const { container, onSubmit } = renderForm(judgingDraft(), { presets });

		fireEvent.click(screen.getByTestId("benchmark-rubric-add"));
		save(container);

		expect(onSubmit).not.toHaveBeenCalled();
		expect(
			screen.getAllByText("An id may only contain lowercase letters, digits, - and _ (32 characters max).").length,
		).toBeGreaterThan(0);
	});

	it("derives a criterion id from its title until the operator edits the id", () => {
		const { container, onSubmit } = renderForm(judgingDraft({ rubric: rubric([{ id: "", title: "", description: "d", weight: 10 }]) }), {
			presets,
		});

		fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Tone of voice" } });
		save(container);

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(
			expect.objectContaining({ rubric: rubric([{ id: "tone-of-voice", title: "Tone of voice", description: "d", weight: 10 }]) }),
		);
	});

	// A frozen project has runs: its task is fixed forever, but its judge is not.
	it("keeps the task read-only on a frozen project while the judge stays editable", () => {
		renderForm(judgingDraft(), { frozen: true, presets, onCancel: vi.fn() });

		expect((screen.getByRole("textbox", { name: /Name/ }) as HTMLInputElement).disabled).toBe(true);
		expect((screen.getByRole("checkbox", { name: "Enable automated judge" }) as HTMLInputElement).disabled).toBe(false);
		expect(screen.getByTestId("benchmark-rubric-add")).toBeTruthy();
		expect(screen.getByRole("button", { name: "Save judge" })).toBeTruthy();
	});

	it("offers Cancel alongside Save while editable", () => {
		const onCancel = vi.fn();
		renderForm(draft(), { onCancel });

		fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

		expect(onCancel).toHaveBeenCalledOnce();
		expect(screen.getByRole("button", { name: "Save" })).toBeTruthy();
	});
});
