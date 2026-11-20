// @vitest-environment jsdom

// The page's links between tabs, with each panel replaced by a stub that shows the intent it received and offers the
// jump its real counterpart offers: a jump selects the target tab and hands that tab, and only that tab, its intent.
// The route drives the tab through `?tab=`; a small stateful host stands in for it here, as the page is router-free.

import { act, fireEvent, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import {
	type ModelLabIntent,
	type ModelLabJump,
	type ModelLabTab,
	modelLabSearchSchema,
} from "@/features/modelLab/models/ModelLabNavigation";
import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";
import { renderWithProviders } from "@/test/RenderWithProviders";

const jumps = vi.hoisted(() => ({
	architecture: { tab: "architecture", modelName: "org/Tiny-GGUF:Q4_K_M", traceId: "trace-1", blockIndex: 6 },
	nextToken: { tab: "nextToken", modelName: "org/Tiny-GGUF:Q4_K_M", prompt: "hello", disableThinking: true },
	trace: { tab: "trace", experimentId: "trace-1" },
}));

function Stub({
	name,
	intent,
	onJump,
	offers,
}: {
	name: string;
	intent?: ModelLabIntent;
	onJump?: (to: ModelLabJump) => void;
	offers: ModelLabJump[];
}) {
	return (
		<div data-testid={`stub-${name}`} data-intent={intent ? JSON.stringify(intent) : ""}>
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
vi.mock("@/features/modelLab/components/InstalledModelInspector", () => ({ InstalledModelInspector: () => null }));
vi.mock("@/features/modelLab/components/NextTokenExplorer", () => ({
	NextTokenExplorer: (props: { intent?: ModelLabIntent }) => <Stub name="nextToken" {...props} offers={[]} />,
}));
vi.mock("@/features/modelLab/components/TraceExperiments", () => ({
	TraceExperiments: (props: { intent?: ModelLabIntent; onJump?: (to: ModelLabJump) => void }) => (
		<Stub name="trace" {...props} offers={[jumps.architecture as ModelLabJump, jumps.nextToken as ModelLabJump]} />
	),
}));
vi.mock("@/features/modelLab/components/ArchitectureExplorer", () => ({
	ArchitectureExplorer: (props: { intent?: ModelLabIntent; onJump?: (to: ModelLabJump) => void }) => (
		<Stub name="architecture" {...props} offers={[jumps.trace as ModelLabJump]} />
	),
}));

// The page suspends on its strings' import, and React retries that inside an awaited act scope only.
async function renderPage(): Promise<void> {
	await act(async () => {
		renderWithProviders(<ModelLabPage />);
	});
}

// Stands in for the route adapter: it owns `?tab=` and records every change the page asks for.
async function renderRoutedPage(initialTab: ModelLabTab | undefined, changes: ModelLabTab[]): Promise<void> {
	function RoutedPage() {
		const [tab, setTab] = useState(initialTab);
		return (
			<ModelLabPage
				tab={tab}
				onTabChange={(next) => {
					changes.push(next);
					setTab(next);
				}}
			/>
		);
	}
	await act(async () => {
		renderWithProviders(<RoutedPage />);
	});
}

function selectedTab(): string | null {
	return screen.getByRole("tab", { selected: true }).textContent;
}

function intentOf(name: string): unknown {
	const raw = screen.getByTestId(`stub-${name}`).dataset["intent"];
	return raw ? JSON.parse(raw) : undefined;
}

describe("ModelLabPage links between tabs", () => {
	it("opens each link's tab with its intent, numbering every jump", async () => {
		await renderPage();
		fireEvent.click(await screen.findByRole("tab", { name: "Trace and steer" }));

		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));

		expect(screen.getByRole("tab", { name: "Architecture" }).getAttribute("aria-selected")).toBe("true");
		expect(intentOf("architecture")).toEqual({ ...jumps.architecture, nonce: 1 });
		expect(intentOf("trace")).toBeUndefined();

		fireEvent.click(screen.getByRole("button", { name: "architecture to trace" }));

		expect(screen.getByRole("tab", { name: "Trace and steer" }).getAttribute("aria-selected")).toBe("true");
		expect(intentOf("trace")).toEqual({ ...jumps.trace, nonce: 2 });
		expect(intentOf("architecture")).toBeUndefined();

		fireEvent.click(screen.getByRole("button", { name: "trace to nextToken" }));

		expect(screen.getByRole("tab", { name: "Next token" }).getAttribute("aria-selected")).toBe("true");
		expect(intentOf("nextToken")).toEqual({ ...jumps.nextToken, nonce: 3 });
	});

	it("still switches tabs by hand", async () => {
		await renderPage();

		fireEvent.click(await screen.findByRole("tab", { name: "Architecture" }));

		expect(screen.getByRole("tab", { name: "Architecture" }).getAttribute("aria-selected")).toBe("true");
		expect(intentOf("architecture")).toBeUndefined();
	});
});

describe("ModelLabPage tab from the URL", () => {
	it("opens the tab the URL names and reports every switch back to it", async () => {
		const changes: ModelLabTab[] = [];
		await renderRoutedPage("architecture", changes);

		expect(selectedTab()).toBe("Architecture");

		fireEvent.click(screen.getByRole("button", { name: "architecture to trace" }));
		expect(selectedTab()).toBe("Trace and steer");

		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		expect(selectedTab()).toBe("Inspect");

		expect(changes).toEqual(["trace", "inspect"]);
	});

	it("keeps each panel's state across repeated jumps", async () => {
		const changes: ModelLabTab[] = [];
		await renderRoutedPage("trace", changes);
		fireEvent.change(screen.getByRole("textbox", { name: "trace note" }), { target: { value: "kept" } });

		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));
		fireEvent.click(screen.getByRole("button", { name: "architecture to trace" }));
		fireEvent.click(screen.getByRole("button", { name: "trace to architecture" }));

		expect(selectedTab()).toBe("Architecture");
		expect(intentOf("architecture")).toEqual({ ...jumps.architecture, nonce: 3 });
		expect(changes).toEqual(["architecture", "trace", "architecture"]);
		expect((screen.getByRole("textbox", { name: "trace note", hidden: true }) as HTMLInputElement).value).toBe("kept");
	});

	it("falls back to the first tab when the URL names an unknown one", async () => {
		const search = modelLabSearchSchema.parse({ tab: "bogus" });
		expect(search.tab).toBeUndefined();

		await renderRoutedPage(search.tab, []);

		expect(selectedTab()).toBe("Same-prompt A/B");
	});
});
