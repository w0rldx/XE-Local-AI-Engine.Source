// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { RunningModel } from "@/core/loaded-models/models/RunningModelsModels";
import { RunningModelsPanel } from "@/features/loaded-models/components/RunningModelsPanel";
import de from "@/locales/de.json";
import { renderWithMantine, testMantineTheme } from "@/test/MantineTestRender";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderPanel(model: RunningModel): void {
	renderWithProviders(
		<RunningModelsPanel
			runningModels={[model]}
			isLoading={false}
			error={null}
			onEject={() => undefined}
			ejectingModelName={null}
		/>,
	);
}

describe("RunningModelsPanel detail", () => {
	afterEach(async () => {
		cleanup();
		// The i18next instance is shared across this file's tests; undo the German switch.
		await i18next.changeLanguage("en");
	});

	it("translates a known detail code instead of echoing the server's English text", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		renderPanel({
			modelName: "m",
			role: "chat",
			isResponsive: true,
			detail: "Responsive.",
			detailCode: "responsive",
			isBusy: false,
			lastUsedUtc: null,
			isTransient: false,
			effectiveContextTokens: null,
			expertsOffloaded: false,
		});

		expect(screen.getByTestId("loaded-models-llamacpp-detail-m").textContent).toBe("Antwortet auf Statusprüfungen.");
	});

	it("shows the loaded window and an expert offload in the operator's language", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		renderPanel({
			modelName: "m",
			role: "chat",
			isResponsive: true,
			detail: "Responsive.",
			detailCode: "responsive",
			isBusy: false,
			lastUsedUtc: null,
			isTransient: false,
			effectiveContextTokens: 65536,
			expertsOffloaded: true,
		});

		expect(screen.getByTestId("loaded-models-llamacpp-context-m").textContent).toBe("Kontextfenster: 65.536 Tokens");
		expect(screen.getByTestId("loaded-models-llamacpp-experts-offloaded-m").textContent).toBe("Experten im Arbeitsspeicher");
	});

	it("claims neither a window nor an offload it was not told about", () => {
		renderPanel({
			modelName: "m",
			role: "chat",
			isResponsive: true,
			detail: "Responsive.",
			detailCode: "responsive",
			isBusy: false,
			lastUsedUtc: null,
			isTransient: false,
			effectiveContextTokens: null,
			expertsOffloaded: false,
		});

		expect(screen.queryByTestId("loaded-models-llamacpp-context-m")).toBeNull();
		expect(screen.queryByTestId("loaded-models-llamacpp-experts-offloaded-m")).toBeNull();
	});

	it("falls back to the raw detail for a code this build does not know", () => {
		renderPanel({
			modelName: "m",
			role: "chat",
			isResponsive: false,
			detail: "Something new.",
			detailCode: null,
			isBusy: false,
			lastUsedUtc: null,
			isTransient: false,
			effectiveContextTokens: null,
			expertsOffloaded: false,
		});

		expect(screen.getByTestId("loaded-models-llamacpp-detail-m").textContent).toBe("Something new.");
	});
});

function running(modelName: string, overrides: Partial<RunningModel> = {}): RunningModel {
	return {
		modelName,
		role: "chat",
		isResponsive: true,
		detail: "",
		detailCode: "responsive",
		isBusy: false,
		lastUsedUtc: null,
		isTransient: false,
		effectiveContextTokens: null,
		expertsOffloaded: false,
		...overrides,
	};
}

describe("RunningModelsPanel eject focus", () => {
	afterEach(() => {
		cleanup();
	});

	function panel(
		models: readonly RunningModel[],
		onEject: (model: RunningModel) => void,
		ejectingModelName: string | null = null,
	) {
		return (
			<RunningModelsPanel
				runningModels={models}
				isLoading={false}
				error={null}
				onEject={onEject}
				ejectingModelName={ejectingModelName}
			/>
		);
	}

	// rerender replaces the whole tree, so it must carry the provider renderWithMantine put around the first render.
	function withMantine(ui: ReturnType<typeof panel>) {
		return (
			<MantineProvider env="test" theme={testMantineTheme}>
				{ui}
			</MantineProvider>
		);
	}

	it("hands focus to the panel heading when the focused row goes away, never to another row's Eject", () => {
		const onEject = vi.fn();
		const { rerender } = renderWithMantine(panel([running("alpha"), running("beta")], onEject));
		screen.getByTestId("loaded-models-llamacpp-eject-alpha").focus();

		rerender(withMantine(panel([running("beta")], onEject)));

		const heading = screen.getByRole("heading", { name: "llama.cpp runtime" });
		expect(document.activeElement).toBe(heading);
		expect(heading.getAttribute("tabindex")).toBe("-1");
	});

	it("leaves focus alone when the focused row goes away after focus left it for the body", async () => {
		const onEject = vi.fn();
		const { rerender } = renderWithMantine(panel([running("alpha"), running("beta")], onEject));
		const eject = screen.getByTestId("loaded-models-llamacpp-eject-alpha");
		eject.focus();
		// A click on blank page space: focus goes to the body while the Eject stays mounted.
		eject.blur();
		expect(eject.isConnected).toBe(true);
		// The panel tells this apart from a removal one frame after the blur.
		await act(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));

		rerender(withMantine(panel([running("beta")], onEject)));

		expect(document.activeElement).toBe(document.body);
		expect(document.activeElement).not.toBe(screen.getByRole("heading", { name: "llama.cpp runtime" }));
	});

	it("leaves focus alone when a row goes away while focus was never in the panel", () => {
		const onEject = vi.fn();
		const { rerender } = renderWithMantine(panel([running("alpha"), running("beta")], onEject));

		rerender(withMantine(panel([running("beta")], onEject)));

		expect(document.activeElement).toBe(document.body);
	});

	it("keeps focus on the ejecting button, shows its loader and ignores a repeat", () => {
		const onEject = vi.fn();
		const { rerender } = renderWithMantine(panel([running("alpha")], onEject));
		const button = screen.getByTestId("loaded-models-llamacpp-eject-alpha");
		button.focus();

		rerender(withMantine(panel([running("alpha")], onEject, "alpha")));
		fireEvent.click(button);

		expect(onEject).not.toHaveBeenCalled();
		expect(button.getAttribute("aria-busy")).toBe("true");
		expect(button.getAttribute("aria-disabled")).toBe("true");
		expect(button.querySelector(".mantine-Loader-root")).not.toBeNull();
		expect(button).toHaveProperty("disabled", false);
		expect(document.activeElement).toBe(button);
	});

	it("keeps a busy model ejectable here: the graceful eject waits for its response", () => {
		const onEject = vi.fn();
		renderWithMantine(panel([running("alpha", { isBusy: true })], onEject));

		const button = screen.getByTestId("loaded-models-llamacpp-eject-alpha");
		fireEvent.click(button);

		expect(button.hasAttribute("aria-disabled")).toBe(false);
		expect(onEject).toHaveBeenCalledTimes(1);
	});
});
