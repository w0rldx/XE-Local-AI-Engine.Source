// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DirectInferenceSamplingFields } from "@/features/directInference/components/DirectInferenceSamplingFields";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("DirectInferenceSamplingFields", () => {
	afterEach(() => cleanup());

	it("keeps a typed newline so a second stop sequence can follow, and reports only non-empty lines", () => {
		const onChange = vi.fn();
		renderWithProviders(<DirectInferenceSamplingFields value={{}} onChange={onChange} />);
		const stop = screen.getByLabelText("Stop sequences") as HTMLTextAreaElement;

		fireEvent.change(stop, { target: { value: "END\n" } });

		expect(stop.value).toBe("END\n");
		expect(onChange).toHaveBeenLastCalledWith({ stop: ["END"] });

		fireEvent.change(stop, { target: { value: "END\n\nSTOP" } });

		expect(stop.value).toBe("END\n\nSTOP");
		expect(onChange).toHaveBeenLastCalledWith({ stop: ["END", "STOP"] });
	});
});
