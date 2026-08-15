// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkProjectForm } from "@/features/benchmarks/components/BenchmarkProjectForm";
import type { BenchmarkEligibleModel, BenchmarkProjectDraft } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Validation here is deliberately attempt-gated: errors stay hidden until the first submit, so a fresh form is not
// red before the operator has typed anything. The judge model requirement is conditional — it only applies once
// judging is enabled — and a frozen project (runs exist) renders read-only with no way to submit at all.

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

function draft(overrides: Partial<BenchmarkProjectDraft> = {}): BenchmarkProjectDraft {
	return {
		name: "Summarisation",
		coreTask: "Summarise the attached text.",
		contextTokens: 4096,
		agentDefinitionId: "agent-1",
		judgeEnabled: false,
		judgeModelName: null,
		judgeContextTokens: null,
		judgePromptVersion: 1,
		judgeOutputSchemaVersion: 1,
		...overrides,
	};
}

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

	// The judge model is required only while judging is on — an off judge must not block a save.
	it("requires a judge model only once judging is enabled", () => {
		const { container, onSubmit, unmount } = renderForm(draft({ judgeEnabled: false, judgeModelName: null }));
		save(container);
		expect(onSubmit).toHaveBeenCalledOnce();
		unmount();

		const enabled = renderForm(draft({ judgeEnabled: true, judgeModelName: null }));
		save(enabled.container);

		expect(enabled.onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText("Select a judge model.")).toBeTruthy();
	});

	it("submits once the judge model is chosen", () => {
		const values = draft({ judgeEnabled: true, judgeModelName: "judge.gguf", judgeContextTokens: 8192 });
		const { container, onSubmit } = renderForm(values);

		save(container);

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith(values);
	});

	// A frozen project (runs exist) is read-only: no Save, no Cancel, and nothing editable.
	it("hides the actions and disables the inputs when frozen", () => {
		renderForm(draft(), { disabled: true, onCancel: vi.fn() });

		expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
		expect(screen.queryByRole("button", { name: "Cancel" })).toBeNull();
		expect((screen.getByRole("textbox", { name: /Name/ }) as HTMLInputElement).disabled).toBe(true);
	});

	it("offers Cancel alongside Save while editable", () => {
		const onCancel = vi.fn();
		renderForm(draft(), { onCancel });

		fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

		expect(onCancel).toHaveBeenCalledOnce();
		expect(screen.getByRole("button", { name: "Save" })).toBeTruthy();
	});
});
