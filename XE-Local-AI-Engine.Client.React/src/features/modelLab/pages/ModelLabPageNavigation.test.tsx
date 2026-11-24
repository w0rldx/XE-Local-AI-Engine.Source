// @vitest-environment jsdom

// The page's context and its links between tabs, with each panel replaced by a stub that shows the props it received and
// offers the jump its real counterpart offers. Without a route the page keeps its own search; with one it reports every
// change, and whether it pushes, to the route. The real route and history: ModelLabRoutePage.test.tsx.

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import {
	applyModelLabPatch,
	type ModelLabIntent,
	type ModelLabJump,
	type ModelLabSearch,
	type ModelLabUpdate,
	modelLabSearchSchema,
} from "@/features/modelLab/models/ModelLabNavigation";
import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const tiny = "org/Tiny-GGUF:Q4_K_M";
const other = "org/Other-GGUF:Q8_0";
const traceId = "11111111-1111-4111-8111-111111111111";

const jumps = vi.hoisted(() => ({
	architecture: {
		tab: "architecture",
		modelName: "org/Tiny-GGUF:Q4_K_M",
		traceId: "11111111-1111-4111-8111-111111111111",
		blockIndex: 6,
	},
	nextToken: { tab: "nextToken", modelName: "org/Tiny-GGUF:Q4_K_M", prompt: "hello", disableThinking: true },
	trace: { tab: "trace", experimentId: "11111111-1111-4111-8111-111111111111" },
}));

function Stub({
	name,
	shown,
	onJump,
	offers,
}: {
	name: string;
	shown: Record<string, unknown>;
	onJump?: (to: ModelLabJump) => void;
	offers: ModelLabJump[];
}) {
	return (
		<div data-testid={`stub-${name}`} data-shown={JSON.stringify(shown)}>
			{/* Uncontrolled, so a remount would lose what was typed: the panel state the tab switches must keep. */}
			<input aria-label={`${name} note`} />
			{offers.map((jump) => (
				<button key={jump.tab} type="button" onClick={() => onJump?.(jump)}>
					{`${name} to ${jump.tab}`}
				</button>
			))}
		</div>
	);
}

vi.mock("@/features/modelLab/components/SamePromptComparison", () => ({ SamePromptComparison: () => null }));
vi.mock("@/features/modelLab/components/InstalledModelInspector", () => ({
	InstalledModelInspector: ({ modelName }: { modelName: string | null }) => (
		<Stub name="inspect" shown={{ modelName }} offers={[]} />
	),
}));
vi.mock("@/features/modelLab/components/NextTokenExplorer", () => ({
	NextTokenExplorer: ({ modelName, intent }: { modelName: string | null; intent?: ModelLabIntent }) => (
		<Stub name="nextToken" shown={{ modelName, intent }} offers={[]} />
	),
}));
vi.mock("@/features/modelLab/components/TraceExperiments", () => ({
	TraceExperiments: ({ experimentId, onJump }: { experimentId?: string; onJump?: (to: ModelLabJump) => void }) => (
		<Stub
			name="trace"
			shown={{ experimentId }}
			onJump={onJump}
			offers={[jumps.architecture as ModelLabJump, jumps.nextToken as ModelLabJump]}
		/>
	),
}));
vi.mock("@/features/modelLab/components/ArchitectureExplorer", () => ({
	ArchitectureExplorer: ({
		modelName,
		selection,
		traceId,
		onJump,
	}: {
		modelName: string | null;
		selection: unknown;
		traceId: string | null;
		onJump?: (to: ModelLabJump) => void;
	}) => (
		<Stub name="architecture" shown={{ modelName, selection, traceId }} onJump={onJump} offers={[jumps.trace as ModelLabJump]} />
	),
}));

setupMswServer();

function stubModels(names: string[] = [tiny, other]): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				items: names.map((modelName) => ({
					modelName,
					displayLabel: null,
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isReasoningCapable: false,
					isToolCapable: true,
					isOverridden: false,
					provider: "llamacpp",
				})),
			}),
		),
	);
}

// The page suspends on its strings' import, and React retries that inside an awaited act scope only.
async function renderPage(): Promise<void> {
	await act(async () => {
		renderWithProviders(<ModelLabPage />);
	});
	await screen.findByRole("tab", { selected: true });
}

// Stands in for the route adapter: it owns the search, applies each change as the route does, and records it.
async function renderRoutedPage(initial: ModelLabSearch, changes: { search: ModelLabSearch; push: boolean }[]): Promise<void> {
	function RoutedPage() {
		const [search, setSearch] = useState(initial);
		return (
			<ModelLabPage
				search={search}
				onSearchChange={(update: ModelLabUpdate, push: boolean) => {
					const next = applyModelLabPatch(search, update);
					changes.push({ search: next, push });
					setSearch(next);
				}}
			/>
		);
	}
	await act(async () => {
		renderWithProviders(<RoutedPage />);
	});
	await screen.findByRole("tab", { selected: true });
}

function selectedTab(): string | null {
	return screen.getByRole("tab", { selected: true }).textContent;
}

function shownTo(name: string): Record<string, unknown> {
	return JSON.parse(screen.getByTestId(`stub-${name}`).dataset["shown"] ?? "{}") as Record<string, unknown>;
}

async function pickModel(name: string): Promise<void> {
	const select = screen.getByRole("combobox", { name: /Model for Architecture/ });
	fireEvent.click(select);
	const listbox = await waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
	fireEvent.click(await within(listbox).findByRole("option", { name, hidden: true }));
}

describe("ModelLabPage links between tabs", () => {
	it("opens on Architecture, the first tab, with no model", async () => {
		stubModels();
		await renderPage();

		expect(screen.getAllByRole("tab")[0]?.textContent).toBe("Architecture");
		expect(selectedTab()).toBe("Architecture");
		expect(shownTo("architecture")).toEqual({ modelName: null, selection: null, traceId: null });
	});

	it("opens each link's tab with what it names, and hands the Next token prompt only to its shown tab", async () => {
		stubModels();
		await renderPage();
		fireEvent.click(screen.getByRole("tab", { name: "Trace and steer" }));

		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));

		expect(selectedTab()).toBe("Architecture");
		await waitFor(() => expect(shownTo("architecture")).toEqual({ modelName: tiny, selection: 6, traceId }));

		fireEvent.click(screen.getByRole("button", { name: "architecture to trace" }));

		expect(selectedTab()).toBe("Trace and steer");
		expect(shownTo("trace")).toEqual({ experimentId: traceId });
		expect(shownTo("nextToken")).toEqual({ modelName: tiny });

		fireEvent.click(screen.getByRole("button", { name: "trace to nextToken" }));

		expect(selectedTab()).toBe("Next token");
		expect(shownTo("nextToken")).toEqual({ modelName: tiny, intent: { ...jumps.nextToken, nonce: 3 } });

		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));
		fireEvent.click(screen.getByRole("tab", { name: "Next token" }));

		// A tab switch ends the link: the prompt is not handed over again.
		expect(shownTo("nextToken")).toEqual({ modelName: tiny });
	});

	it("moves focus from the jump to the target's tab", async () => {
		stubModels();
		await renderPage();

		fireEvent.click(screen.getByRole("button", { name: "architecture to trace" }));

		expect(document.activeElement).toBe(screen.getByRole("tab", { name: "Trace and steer" }));
	});

	it("still switches tabs by hand", async () => {
		stubModels();
		await renderPage();

		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));

		expect(selectedTab()).toBe("Inspect");
	});
});

describe("ModelLabPage model for three tabs", () => {
	it("hands one pick to Architecture and Next token, and to Inspect only once its tab shows it", async () => {
		stubModels();
		await renderPage();

		await pickModel(tiny);

		await waitFor(() => expect(shownTo("architecture")["modelName"]).toBe(tiny));
		expect(shownTo("nextToken")).toEqual({ modelName: tiny });
		expect(shownTo("inspect")).toEqual({ modelName: null });

		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		expect(shownTo("inspect")).toEqual({ modelName: tiny });

		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));
		await pickModel(other);

		await waitFor(() => expect(shownTo("architecture")["modelName"]).toBe(other));
		// Inspect keeps the file it read until it is shown again.
		expect(shownTo("inspect")).toEqual({ modelName: tiny });
	});

	it("inspects a linked model at once when the link opens Inspect", async () => {
		stubModels();
		await renderRoutedPage({ tab: "inspect", model: other }, []);

		await waitFor(() => expect(shownTo("inspect")).toEqual({ modelName: other }));
	});

	it("drops the last model's block and trace on another pick", async () => {
		stubModels();
		const changes: { search: ModelLabSearch; push: boolean }[] = [];
		await renderRoutedPage({ model: tiny, block: 3, trace: traceId, level: "math" }, changes);
		await waitFor(() => expect(shownTo("architecture")).toEqual({ modelName: tiny, selection: 3, traceId }));

		await pickModel(other);

		expect(changes).toEqual([{ search: { model: other, level: "math" }, push: false }]);
		expect(shownTo("architecture")).toEqual({ modelName: other, selection: null, traceId: null });
	});
});

describe("ModelLabPage search from the URL", () => {
	it("opens the tab the URL names, pushes only a jump, and replaces a tab switch", async () => {
		stubModels();
		const changes: { search: ModelLabSearch; push: boolean }[] = [];
		await renderRoutedPage({ tab: "trace" }, changes);

		expect(selectedTab()).toBe("Trace and steer");

		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));
		expect(selectedTab()).toBe("Architecture");

		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		expect(selectedTab()).toBe("Inspect");

		expect(changes).toEqual([
			{ search: { model: tiny, trace: traceId, block: 6 }, push: true },
			{ search: { model: tiny, trace: traceId, block: 6, tab: "inspect" }, push: false },
		]);
	});

	it("keeps each panel's state across repeated jumps", async () => {
		stubModels();
		await renderRoutedPage({ tab: "trace" }, []);
		fireEvent.change(screen.getByRole("textbox", { name: "trace note" }), { target: { value: "kept" } });

		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));
		fireEvent.click(screen.getByRole("button", { name: "architecture to trace" }));
		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));

		expect(selectedTab()).toBe("Architecture");
		expect((screen.getByRole("textbox", { name: "trace note", hidden: true }) as HTMLInputElement).value).toBe("kept");
	});

	it("falls back to Architecture when the URL names an unknown tab", async () => {
		stubModels();
		const search = modelLabSearchSchema.parse({ tab: "bogus" });
		expect(search.tab).toBeUndefined();

		await renderRoutedPage(search, []);

		expect(selectedTab()).toBe("Architecture");
	});
});
