// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// Monaco is ~3 MB behind a lazy import and needs a layout engine jsdom does not have. What is under test here is the
// pythonTests CONFIGURATION, not the editing surface, so the shared code editor is stood in for by a textarea.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({
		value,
		onChange,
		"data-testid": testId,
	}: {
		value: string;
		onChange?: (next: string) => void;
		"data-testid"?: string;
	}) => <textarea data-testid={testId} value={value} onChange={(event) => onChange?.(event.currentTarget.value)} />,
}));

import { BenchmarkVerifierEditor } from "@/features/benchmarks/components/BenchmarkVerifierEditor";
import type { BenchmarkCriterionKind } from "@/features/benchmarks/models/BenchmarkVerifier";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The editor's job is to keep the stored blob a valid config OF ITS KIND at every keystroke. A regex pattern left
// behind on a criterion switched to `exact` is a policy the node refuses with a message that names neither field.

afterEach(cleanup);

function renderEditor(kind: BenchmarkCriterionKind, config: string | null, issue = null) {
	const onChange = vi.fn();
	renderWithProviders(
		<BenchmarkVerifierEditor kind={kind} config={config} issue={issue} onChange={onChange} testId="verifier" />,
	);
	return onChange;
}

describe("BenchmarkVerifierEditor", () => {
	it("offers every kind the node accepts", () => {
		renderEditor("llm", null);

		expect((screen.getByTestId("verifier-kind") as HTMLInputElement).value).toBe("The judge model");
	});

	it("renders no configuration at all for an llm criterion", () => {
		renderEditor("llm", null);

		expect(screen.queryByTestId("verifier-expected")).toBeNull();
		expect(screen.queryByTestId("verifier-pattern")).toBeNull();
	});

	it("edits the expected answer of an exact criterion", () => {
		const onChange = renderEditor("exact", '{"expected":""}');

		fireEvent.change(screen.getByTestId("verifier-expected"), { target: { value: "42" } });

		expect(onChange).toHaveBeenCalledWith({ kind: "exact", config: '{"expected":"42"}' });
	});

	// `trim` is the node's only default-on normalisation flag; an unset config must read as on for it and off for the
	// rest, or the checkbox row would claim a normalisation the node is not applying.
	it("reflects the node's own normalisation defaults on an unset config", () => {
		renderEditor("exact", '{"expected":"42"}');

		expect((screen.getByTestId("verifier-normalize-trim") as HTMLInputElement).checked).toBe(true);
		expect((screen.getByTestId("verifier-normalize-caseInsensitive") as HTMLInputElement).checked).toBe(false);
	});

	it("edits a regex pattern and its must-match flag", () => {
		const onChange = renderEditor("regex", '{"pattern":"","mustMatch":true}');

		fireEvent.change(screen.getByTestId("verifier-pattern"), { target: { value: "^42$" } });

		expect(onChange).toHaveBeenCalledWith({ kind: "regex", config: '{"pattern":"^42$","mustMatch":true}' });
	});

	// A regex pattern is not an expected answer: carrying the old keys across a kind change would send the node a blob
	// it cannot parse as the new kind, and the refusal would name neither the old field nor the new one.
	it("replaces the configuration wholesale when the kind changes", () => {
		const onChange = renderEditor("regex", '{"pattern":"^42$"}');

		// Click, not `change`: Mantine 9.5.2 keeps the Popover dropdown mounted with `display: none` until the
		// control is actually opened, so an option only enters the accessibility tree once the input is clicked.
		fireEvent.click(screen.getByTestId("verifier-kind"));
		fireEvent.click(screen.getByRole("option", { name: "Exact answer" }));

		expect(onChange).toHaveBeenCalledWith({ kind: "exact", config: '{"expected":""}' });
	});

	// The node reads an ABSENT member as "not constrained" and a present empty one as a constraint nothing satisfies.
	it("removes an emptied optional field rather than storing it empty", () => {
		const onChange = renderEditor("constraint", '{"minWords":10,"maxWords":50}');

		fireEvent.change(screen.getByTestId("verifier-minWords"), { target: { value: "" } });

		expect(onChange).toHaveBeenCalledWith({ kind: "constraint", config: '{"maxWords":50}' });
	});

	// Emptying the LAST constraint leaves no config at all, not "{}": an empty object is a config, and the node reads
	// one on a criterion that states no constraint as a refusal rather than as "unconstrained".
	it("drops the config entirely once the last constraint is cleared", () => {
		const onChange = renderEditor("constraint", '{"minWords":10}');

		fireEvent.change(screen.getByTestId("verifier-minWords"), { target: { value: "" } });

		expect(onChange).toHaveBeenCalledWith({ kind: "constraint", config: null });
	});

	it("shows the pre-check failure on the field it belongs to", () => {
		renderWithProviders(
			<BenchmarkVerifierEditor kind="regex" config='{"pattern":""}' issue="patternRequired" onChange={vi.fn()} testId="verifier" />,
		);

		expect(screen.getByTestId("verifier").textContent).toContain("State a pattern.");
	});

	it("states which schema keywords this build actually enforces", () => {
		renderEditor("jsonSchema", '{"schema":{"type":"object"}}');

		expect(screen.getByTestId("verifier").textContent).toContain("additionalProperties");
	});
});

// pythonTests is the one kind whose configuration is a PROGRAM. The editor's job is the same as for every other kind:
// keep the stored blob a valid config of its kind at every keystroke, and say what the node would refuse.
describe("BenchmarkVerifierEditor pythonTests", () => {
	it("edits the operator's test code", () => {
		const onChange = renderEditor("pythonTests", '{"testCode":""}');

		fireEvent.change(screen.getByTestId("verifier-test-code"), { target: { value: "assert candidate.solve(2) == 4" } });

		expect(onChange).toHaveBeenCalledWith({ kind: "pythonTests", config: '{"testCode":"assert candidate.solve(2) == 4"}' });
	});

	it("edits the exports the tests may call by bare name", () => {
		const onChange = renderEditor("pythonTests", '{"testCode":"assert solve(2) == 4"}');

		// TagsInput commits on Enter only while its own combobox is open (Mantine 9.5.2), so open it first.
		fireEvent.click(screen.getByTestId("verifier-exports"));
		fireEvent.change(screen.getByTestId("verifier-exports"), { target: { value: "solve" } });
		fireEvent.keyDown(screen.getByTestId("verifier-exports"), { key: "Enter" });

		expect(onChange).toHaveBeenCalledWith({
			kind: "pythonTests",
			config: '{"testCode":"assert solve(2) == 4","exports":["solve"]}',
		});
	});

	// The process boundary is the whole soundness argument for this kind, so the form states it rather than leaving an
	// operator to assume their tests and the model's code share an interpreter.
	it("says where the answer's code runs and where the tests run", () => {
		renderEditor("pythonTests", '{"testCode":"assert True"}');

		expect(screen.getByTestId("verifier-python").textContent).toContain("untrusted child process");
	});

	it("renders none of the other kinds' fields", () => {
		renderEditor("pythonTests", '{"testCode":"assert True"}');

		expect(screen.queryByTestId("verifier-expected")).toBeNull();
		expect(screen.queryByTestId("verifier-pattern")).toBeNull();
	});
});
