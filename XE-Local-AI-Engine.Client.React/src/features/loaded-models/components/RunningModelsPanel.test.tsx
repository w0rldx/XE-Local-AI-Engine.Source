// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, describe, expect, it } from "vitest";

import de from "@/locales/de.json";

import { RunningModelsPanel } from "@/features/loaded-models/components/RunningModelsPanel";
import type { RunningModel } from "@/features/loaded-models/models/RunningModelsModels";
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
