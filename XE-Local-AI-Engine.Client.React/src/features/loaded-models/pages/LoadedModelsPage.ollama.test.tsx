// @vitest-environment jsdom

// The XE_OLLAMA_RUNTIME_ENABLED gate reaches the page as `ollamaConfigured:false` on an otherwise healthy-looking
// snapshot — isAvailable:true with an empty list — so the WIRE SHAPE is what has to be pinned. MSW serves it through
// the real query, mapper and en bundle; the sibling LoadedModelsPage.test.tsx stubs the hook, which cannot show that
// the mapper's default survives or that the two empty states stay mutually exclusive.

import { screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

// The page's eject flow reads the confirm context; this file never ejects, and a ConfirmProvider is the only piece of
// the tree renderWithProviders does not supply.
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));

import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";
import { jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// The llama.cpp section is a SECOND runtime on the same page and issues its own request; MSW errors on any route a
// test did not declare, so it has to answer. Empty is enough — this file is about the Ollama section only.
const llamaCppRoute = jsonRoute("get", "model-fit/running", { items: [] });

/** The Ollama snapshot the no-op runtime returns once the gate is off: "nothing there", not "unreachable". */
const gatedOffSnapshot = { isAvailable: true, ollamaConfigured: false, error: null, items: [] };

/** A configured, reachable daemon that simply holds nothing right now. */
const idleSnapshot = { isAvailable: true, ollamaConfigured: true, error: null, items: [] };

describe("LoadedModelsPage — Ollama runtime gate", () => {
	it("names the disabled runtime instead of rendering an idle-daemon empty state", async () => {
		server.use(llamaCppRoute, jsonRoute("get", "models/running", gatedOffSnapshot));

		renderWithProviders(<LoadedModelsPage />);

		const disabled = await screen.findByTestId("loaded-models-runtime-disabled");
		expect(disabled.textContent).toContain("XE_OLLAMA_RUNTIME_ENABLED=false");
		// The operator must not be told llama.cpp is gone too.
		expect(disabled.textContent).toContain("llama.cpp models still appear below");
		// Every isAvailable-driven branch stays silent: no "No models currently loaded.", no unreachable line, no table.
		expect(screen.queryByTestId("loaded-models-empty")).toBeNull();
		expect(screen.queryByTestId("loaded-models-unavailable")).toBeNull();
		expect(screen.queryByTestId("loaded-models-table")).toBeNull();
	});

	it("still renders the ordinary empty state when a configured runtime holds no models", async () => {
		server.use(llamaCppRoute, jsonRoute("get", "models/running", idleSnapshot));

		renderWithProviders(<LoadedModelsPage />);

		const empty = await screen.findByTestId("loaded-models-empty");
		expect(empty.textContent).toContain("No models currently loaded.");
		expect(screen.queryByTestId("loaded-models-runtime-disabled")).toBeNull();
	});
});
