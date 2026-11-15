// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DirectInferenceSamplingFields } from "@/core/directInference/DirectInferenceSamplingFields";
import en from "@/locales/en.json";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("DirectInferenceSamplingFields", () => {
	afterEach(() => cleanup());

	it("states a given default output cap on the empty field, and that thinking counts toward it", () => {
		renderWithProviders(<DirectInferenceSamplingFields value={{}} onChange={vi.fn()} defaultMaxOutputTokens={16384} />);

		const maxOutput = screen.getByLabelText("Max output tokens") as HTMLInputElement;

		expect(maxOutput.placeholder).toBe(`Default: ${(16384).toLocaleString()}`);
		expect(screen.getByText("Thinking counts toward this limit.")).toBeTruthy();
	});

	it("states no default when the model has none", () => {
		renderWithProviders(<DirectInferenceSamplingFields value={{}} onChange={vi.fn()} />);

		const maxOutput = screen.getByLabelText("Max output tokens") as HTMLInputElement;

		expect(maxOutput.placeholder).toBe("Model default");
		expect(screen.queryByText("Thinking counts toward this limit.")).toBeNull();
	});

	it("explains each field in a card beside its label, keeping the inputs' names", async () => {
		renderWithProviders(<DirectInferenceSamplingFields value={{}} onChange={vi.fn()} />);

		const minP = en.pages.chat.samplingOptions.minP;
		expect(screen.getByRole("textbox", { name: minP })).toBeDefined();
		expect(screen.getByRole("textbox", { name: en.pages.chat.samplingOptions.stop })).toBeDefined();
		fireEvent.mouseEnter(screen.getByRole("button", { name: `About ${minP}` }));
		const card = await screen.findByRole("tooltip");
		expect(card.textContent).toContain(en.pages.chat.samplingOptions.minPInfo);
		expect(screen.getByRole("button", { name: `About ${en.pages.chat.samplingOptions.stop}` })).toBeDefined();
	});

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
