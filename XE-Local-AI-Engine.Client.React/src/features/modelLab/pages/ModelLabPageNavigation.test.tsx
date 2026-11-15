// @vitest-environment jsdom

// The page's links between tabs, with each panel replaced by a stub that shows the intent it received and offers the
// jump its real counterpart offers: a jump selects the target tab and hands that tab, and only that tab, its intent.

import { act, fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ModelLabIntent, ModelLabJump } from "@/features/modelLab/models/ModelLabNavigation";
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
