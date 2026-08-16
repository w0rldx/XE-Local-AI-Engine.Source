// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkScorePicker } from "@/features/benchmarks/components/BenchmarkScorePicker";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The operator score is a 0..100 override, and the control's core rule is that NOTHING is written until the operator
// commits: Mantine fires a spurious onChange on mount for a bounded NumberInput/Slider, and an auto-saving control
// would turn that into a real score on a run that deliberately had none. The whole control also locks while a save is
// in flight or the run is not scoreable — a second write would race the run version.

function renderPicker(props: Partial<React.ComponentProps<typeof BenchmarkScorePicker>> = {}) {
	const onChange = vi.fn();
	const onClear = vi.fn();
	const view = renderWithProviders(
		<BenchmarkScorePicker value={null} disabled={false} onChange={onChange} onClear={onClear} {...props} />,
	);
	return { ...view, onChange, onClear };
}

describe("BenchmarkScorePicker", () => {
	afterEach(cleanup);

	// The mount itself must not write. This is the trap the star picker never had and this control does.
	it("writes nothing on mount", () => {
		const { onChange, onClear } = renderPicker();

		expect(onChange).not.toHaveBeenCalled();
		expect(onClear).not.toHaveBeenCalled();
	});

	it("offers the 0..100 presets and reports the clicked one", () => {
		const { onChange } = renderPicker();

		for (const preset of [0, 25, 50, 75, 100]) {
			expect(screen.getByTestId(`benchmark-score-preset-${preset}`)).toBeTruthy();
		}

		fireEvent.click(screen.getByTestId("benchmark-score-preset-75"));

		expect(onChange).toHaveBeenCalledExactlyOnceWith(75);
	});

	it("commits the edited number only once the operator saves", () => {
		const { onChange } = renderPicker();

		fireEvent.change(screen.getByTestId("benchmark-score-input"), { target: { value: "63" } });
		expect(onChange).not.toHaveBeenCalled();

		fireEvent.click(screen.getByTestId("benchmark-score-save"));

		expect(onChange).toHaveBeenCalledExactlyOnceWith(63);
	});

	// A zero score is a real verdict, not "unset": it must be committable and must read back as pressed.
	it("treats zero as a score rather than as an absent override", () => {
		const { onChange } = renderPicker({ value: 0 });

		expect(screen.getByTestId("benchmark-score-preset-0").getAttribute("aria-pressed")).toBe("true");
		fireEvent.click(screen.getByTestId("benchmark-score-preset-0"));

		expect(onChange).toHaveBeenCalledExactlyOnceWith(0);
	});

	it("marks only the stored preset as pressed", () => {
		renderPicker({ value: 50 });

		expect(screen.getByTestId("benchmark-score-preset-50").getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByTestId("benchmark-score-preset-75").getAttribute("aria-pressed")).toBe("false");
	});

	// Clearing is its own operation on the wire (DELETE), never "score 0".
	it("offers Clear override only once an override exists", () => {
		const unset = renderPicker();
		expect((screen.getByTestId("benchmark-score-clear") as HTMLButtonElement).disabled).toBe(true);
		unset.unmount();

		const { onClear, onChange } = renderPicker({ value: 40 });
		fireEvent.click(screen.getByTestId("benchmark-score-clear"));

		expect(onClear).toHaveBeenCalledOnce();
		expect(onChange).not.toHaveBeenCalled();
	});

	// Saving the value already stored would post a no-op against a version that may have moved on.
	it("keeps Save inert while the draft matches the stored score", () => {
		renderPicker({ value: 40 });

		expect((screen.getByTestId("benchmark-score-save") as HTMLButtonElement).disabled).toBe(true);
	});

	it.each([
		["disabled", { disabled: true, isSaving: false }],
		["saving", { disabled: false, isSaving: true }],
	])("locks every control while %s", (_case, props) => {
		const { onChange, onClear } = renderPicker({ value: 40, ...props });

		for (const testId of ["benchmark-score-preset-25", "benchmark-score-save", "benchmark-score-clear"]) {
			expect((screen.getByTestId(testId) as HTMLButtonElement).disabled).toBe(true);
		}

		fireEvent.click(screen.getByTestId("benchmark-score-preset-25"));

		expect(onChange).not.toHaveBeenCalled();
		expect(onClear).not.toHaveBeenCalled();
	});

	it("exposes the control group and its inputs to assistive tech", () => {
		renderPicker();

		expect(screen.getByRole("group", { name: "Operator score" })).toBeTruthy();
		expect(screen.getByLabelText("Operator score, 0 to 100")).toBeTruthy();
	});
});
