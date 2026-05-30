// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

// The tool selector now fetches the catalog via useToolCatalog (loop P4 dynamic catalog). Mock it so the form
// renders the built-in tool rows deterministically without a QueryClient or a real request.
const { useToolCatalogMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
}));

vi.mock("@/features/tools/queries/useToolCatalog", () => ({
	useToolCatalog: useToolCatalogMock,
}));

import { AgentDefinitionForm } from "@/features/agents/components/AgentDefinitionForm";
import type { AgentDefinitionFormValues } from "@/features/agents/models/AgentDefinitionModels";
import type { ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

const catalogTools: ToolCatalogEntry[] = [
	{
		name: "GetCurrentTime",
		description: "Returns the current time.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
	},
	{
		name: "Calculate",
		description: "Evaluates arithmetic.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
	},
];

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
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

const baseValues: AgentDefinitionFormValues = {
	name: "",
	description: "",
	instructions: "",
	modelProfile: null,
	reasoningEffort: null,
	kind: "Single",
	allowedToolNames: [],
	toolApprovals: {},
};

function renderForm(overrides: {
	initialValues?: Partial<AgentDefinitionFormValues>;
	toolCapableModels?: string[];
	onSubmit?: (values: AgentDefinitionFormValues) => void;
}) {
	const onSubmit = overrides.onSubmit ?? vi.fn();
	renderWithProviders(
		<AgentDefinitionForm
			initialValues={{ ...baseValues, ...overrides.initialValues }}
			modelOptions={[
				{ value: "qwen3:8b", label: "qwen3:8b" },
				{ value: "llama3:8b", label: "llama3:8b" },
			]}
			toolCapableModels={overrides.toolCapableModels ?? []}
			isSubmitting={false}
			onSubmit={onSubmit}
			onCancel={vi.fn()}
		/>,
	);
	return { onSubmit };
}

describe("AgentDefinitionForm", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useToolCatalogMock.mockReturnValue({ data: catalogTools, isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the core fields and the tool selector", () => {
		renderForm({});

		expect(screen.getByTestId("agent-form-name")).toBeTruthy();
		expect(screen.getByTestId("agent-form-instructions")).toBeTruthy();
		expect(screen.getByTestId("agent-tool-selector")).toBeTruthy();
	});

	it("disables tool selection and shows a warning when the model is not tool-capable", () => {
		renderForm({
			initialValues: { modelProfile: "llama3:8b" },
			toolCapableModels: ["qwen3:8b"],
		});

		expect(screen.getByTestId("agent-tool-capability-warning")).toBeTruthy();
		const checkbox = screen.getByTestId("agent-tool-checkbox-GetCurrentTime") as HTMLInputElement;
		expect(checkbox.disabled).toBe(true);
	});

	it("enables tool selection when the model is tool-capable", () => {
		renderForm({
			initialValues: { modelProfile: "qwen3:8b" },
			toolCapableModels: ["qwen3:8b"],
		});

		expect(screen.queryByTestId("agent-tool-capability-warning")).toBeNull();
		const checkbox = screen.getByTestId("agent-tool-checkbox-GetCurrentTime") as HTMLInputElement;
		expect(checkbox.disabled).toBe(false);
	});

	it("blocks submit and surfaces required errors when name and instructions are empty", () => {
		const { onSubmit } = renderForm({});

		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText("Name is required")).toBeTruthy();
		expect(screen.getByText("Instructions are required")).toBeTruthy();
	});

	it("submits sanitized values when the form is valid", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "Helper", instructions: "Be helpful" },
		});

		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({ name: "Helper", instructions: "Be helpful", kind: "Single" }),
		);
	});

	it("strips selected tools on submit when the model is not tool-capable", () => {
		const { onSubmit } = renderForm({
			initialValues: {
				name: "Helper",
				instructions: "Be helpful",
				modelProfile: "llama3:8b",
				allowedToolNames: ["GetCurrentTime"],
				toolApprovals: { GetCurrentTime: true },
			},
			toolCapableModels: ["qwen3:8b"],
		});

		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({ allowedToolNames: [], toolApprovals: {} }),
		);
	});
});
