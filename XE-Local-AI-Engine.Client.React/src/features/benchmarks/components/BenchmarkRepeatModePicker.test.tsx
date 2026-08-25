// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkRepeatModePicker } from "@/features/benchmarks/components/BenchmarkRepeatModePicker";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The mode and its temperature are ONE decision: a sampled group with no temperature is not a different default, it is
// a different experiment. The temperature therefore only exists while the mode that uses it is selected.

describe("BenchmarkRepeatModePicker", () => {
	afterEach(cleanup);

	it("hides the temperature in the deterministic mode", () => {
		renderWithProviders(<BenchmarkRepeatModePicker mode="Throughput" temperature={null} onChange={vi.fn()} />);

		expect(screen.queryByTestId("benchmark-answer-variance-temperature")).toBeNull();
		expect(screen.getByTestId("benchmark-repeat-mode")).toHaveProperty("value", "Throughput");
	});

	it("reveals the temperature at the node's default once answers are what varies", () => {
		renderWithProviders(<BenchmarkRepeatModePicker mode="AnswerVariance" temperature={null} onChange={vi.fn()} />);

		expect(screen.getByTestId("benchmark-answer-variance-temperature")).toHaveProperty("value", "0.7");
	});

	it("reports a temperature change without losing the mode", () => {
		const onChange = vi.fn();
		renderWithProviders(<BenchmarkRepeatModePicker mode="AnswerVariance" temperature={0.7} onChange={onChange} />);

		fireEvent.change(screen.getByTestId("benchmark-answer-variance-temperature"), { target: { value: "1.2" } });

		expect(onChange).toHaveBeenCalledWith("AnswerVariance", 1.2);
	});
});
