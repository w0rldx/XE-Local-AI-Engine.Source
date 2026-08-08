// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AgentNodeForm } from "@/features/preview/components/AgentNodeForm";
import type { PreviewCanvasNodeData } from "@/features/preview/models/PreviewCanvasModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

// The form imports agent definitions (for the Import picker) and the local models (for the model picker). Mock
// both so the test exercises the COPY behavior without a network/query layer.
vi.mock("@/features/agents/queries/useAgentDefinitions", () => ({
	useAgentDefinitions: () => ({
		data: [
			{
				id: "def-1",
				name: "Researcher",
				description: "",
				instructions: "You are a meticulous researcher.",
				modelProfile: null,
				reasoningEffort: "high",
				kind: "Single",
				allowedToolNames: [],
				toolApprovals: {},
				allowedSkillIds: [],
				orchestrationTopologyJson: null,
				playbookEnabled: false,
				version: 1,
				createdAtUtc: 0,
				updatedAtUtc: 0,
			},
		],
	}),
}));

vi.mock("@tanstack/react-query", () => ({
	useQuery: () => ({ data: { items: [], isAvailable: false } }),
}));

vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: (options: unknown) => options,
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listLocalModelsOptions: () => ({}),
}));

function renderForm(data: PreviewCanvasNodeData, onChange: (patch: Partial<PreviewCanvasNodeData>) => void) {
	return render(
		<MantineProvider>
			<AgentNodeForm data={data} onChange={onChange} />
		</MantineProvider>,
	);
}

// Mantine reads window.matchMedia; jsdom does not provide it.
function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	// Mantine's Select dropdown (ScrollArea) reads ResizeObserver.
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();
			unobserve = vi.fn();
			disconnect = vi.fn();
		},
	});
	// Mantine's autosize Textarea reads document.fonts.ready.
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

describe("AgentNodeForm", () => {
	beforeEach(installJsdomEnvironmentMocks);
	afterEach(cleanup);

	it("copies the chosen agent definition's instructions into the node on import", () => {
		const onChange = vi.fn();
		renderForm({ kind: "Agent", label: "", instructions: "", model: "" }, onChange);

		// Open the Import select (Mantine puts the data-testid on the input itself) and choose the mocked
		// definition from the rendered option list.
		const importInput = screen.getByTestId("preview-agent-import") as HTMLInputElement;
		fireEvent.click(importInput);
		fireEvent.click(screen.getByText("Researcher"));

		// Import COPIES the definition's instructions (+ label/reasoning) into the node via onChange.
		expect(onChange).toHaveBeenCalledWith(
			expect.objectContaining({
				instructions: "You are a meticulous researcher.",
				label: "Researcher",
				reasoningEffort: "high",
			}),
		);
	});
});
