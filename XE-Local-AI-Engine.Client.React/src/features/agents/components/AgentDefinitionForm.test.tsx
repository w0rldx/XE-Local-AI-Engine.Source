// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { useRef } from "react";
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

// The basic-fields section now embeds the AI-draft affordance, which is server-state backed (installed models +
// running set). Stub it out: this file covers the form's own fields, and the affordance has its own test.
vi.mock("@/features/assist/components/AssistActions", () => ({ AssistActions: () => null }));

// The tool selector now fetches the catalog via useToolCatalog (dynamic tool-catalog dynamic catalog). Mock it so the form
// renders the built-in tool rows deterministically without a QueryClient or a real request.
const { useToolCatalogMock, useSkillsMock } = vi.hoisted(() => ({
	useToolCatalogMock: vi.fn(),
	useSkillsMock: vi.fn(),
}));

vi.mock("@/features/tools/queries/useToolCatalog", () => ({
	useToolCatalog: useToolCatalogMock,
}));

// The skill selector fetches the node skill library via useSkills. Mock it so the form renders without a
// QueryClient or a real request (the skill rows aren't asserted here — see AgentSkillSelector.test.tsx).
vi.mock("@/features/skills/queries/useSkills", () => ({
	useSkills: useSkillsMock,
}));

import { AgentDefinitionForm, type AgentDefinitionFormHandle } from "@/features/agents/components/AgentDefinitionForm";
import type { AgentDefinition, AgentDefinitionFormValues } from "@/features/agents/models/AgentDefinitionModels";
import type { ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

function makeDefinition(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
	return {
		id: "11111111-1111-1111-1111-111111111111",
		name: "Specialist",
		description: "",
		instructions: "Be a specialist",
		modelProfile: null,
		reasoningEffort: null,
		kind: "Single",
		allowedToolNames: [],
		toolApprovals: {},
		allowedSkillIds: [],
		orchestrationTopologyJson: null,
		playbookEnabled: false,
		defaultTemporaryChat: false,
		memoryExtractionEnabled: true,
		disableBaseScaffold: false,
		disableToolRelevanceFilter: false,
		version: 1,
		createdAtUtc: 0,
		updatedAtUtc: 0,
		...overrides,
	};
}

const catalogTools: ToolCatalogEntry[] = [
	{
		name: "GetCurrentTime",
		description: "Returns the current time.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: false,
	},
	{
		name: "Calculate",
		description: "Evaluates arithmetic.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: false,
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
	allowedSkillIds: [],
	orchestration: { participantAgentDefinitionIds: [], handoffs: [], maxTurnsPerAgent: 8, returnToPrevious: false },
	playbookEnabled: false,
	defaultTemporaryChat: false,
	memoryExtractionEnabled: true,
	disableBaseScaffold: false,
	disableToolRelevanceFilter: false,
	generationMetadata: null,
};

function renderForm(overrides: {
	initialValues?: Partial<AgentDefinitionFormValues>;
	toolCapableModels?: string[];
	allDefinitions?: AgentDefinition[];
	selfId?: string;
	onSubmit?: (values: AgentDefinitionFormValues) => void;
}) {
	const onSubmit = overrides.onSubmit ?? vi.fn();

	// The form no longer renders its own Save button — submission is driven imperatively via the handle, exactly as the
	// dialog footer does in AgentsPage. Mirror that with a ref + a test-only Save button so the submit path is exercised.
	function FormHarness() {
		const ref = useRef<AgentDefinitionFormHandle>(null);
		return (
			<>
				<AgentDefinitionForm
					ref={ref}
					initialValues={{ ...baseValues, ...overrides.initialValues }}
					modelOptions={[
						{ value: "qwen3:8b", label: "qwen3:8b" },
						{ value: "llama3:8b", label: "llama3:8b" },
					]}
					toolCapableModels={overrides.toolCapableModels ?? []}
					allDefinitions={overrides.allDefinitions ?? []}
					selfId={overrides.selfId ?? ""}
					onSubmit={onSubmit}
				/>
				<button type="button" onClick={() => ref.current?.submit()} data-testid="agent-form-submit">
					Save
				</button>
			</>
		);
	}

	renderWithProviders(<FormHarness />);
	return { onSubmit };
}

describe("AgentDefinitionForm", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useToolCatalogMock.mockReturnValue({ data: catalogTools, isLoading: false, error: null });
		useSkillsMock.mockReturnValue({ data: [], isLoading: false, error: null });
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

	// Regression guard for the RC crash: onChange read event.currentTarget.value inside a deferred functional
	// updater. React 19 nulls currentTarget after the synchronous handler returns, so the updater read null.value
	// → TypeError. Fix: capture into a local const before the updater (e.g. `const v = e.currentTarget.value`).
	// NOTE: React flushes synchronously in act() so this test cannot reproduce the timing failure; it only guards
	// that the controlled value flows through correctly. A real-browser E2E test is needed to catch the
	// null-currentTarget class of bug (a known, documented coverage gap for this test).
	it("accepts typed input into the multiline instructions field without crashing", () => {
		const { onSubmit } = renderForm({ initialValues: { name: "Helper" } });

		// Instructions is now a MarkdownEditorField; its inner textarea is suffixed "-textarea".
		const instructions = screen.getByTestId("agent-form-instructions-textarea") as HTMLTextAreaElement;
		fireEvent.change(instructions, { target: { value: "You are a careful assistant." } });

		expect(instructions.value).toBe("You are a careful assistant.");
		fireEvent.click(screen.getByTestId("agent-form-submit"));
		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({ name: "Helper", instructions: "You are a careful assistant." }),
		);
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

		expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ allowedToolNames: [], toolApprovals: {} }));
	});

	it("submits playbookEnabled true when the playbook toggle is switched on", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "Helper", instructions: "Be helpful" },
		});

		const toggle = screen.getByTestId("agent-form-playbook-enabled") as HTMLInputElement;
		expect(toggle.checked).toBe(false);
		fireEvent.click(toggle);
		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ playbookEnabled: true }));
	});

	it("hides the default-temporary-chat toggle until adaptive memory is enabled", () => {
		renderForm({ initialValues: { name: "Helper", instructions: "Be helpful", playbookEnabled: false } });

		expect(screen.queryByTestId("agent-form-default-temporary-chat")).toBeNull();

		fireEvent.click(screen.getByTestId("agent-form-playbook-enabled"));

		expect(screen.getByTestId("agent-form-default-temporary-chat")).toBeTruthy();
	});

	it("round-trips defaultTemporaryChat through the form", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "Helper", instructions: "Be helpful", playbookEnabled: true },
		});

		const toggle = screen.getByTestId("agent-form-default-temporary-chat") as HTMLInputElement;
		expect(toggle.checked).toBe(false);
		fireEvent.click(toggle);
		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ defaultTemporaryChat: true }));
	});

	it("hides the memory-extraction toggle until adaptive memory is enabled", () => {
		renderForm({ initialValues: { name: "Helper", instructions: "Be helpful", playbookEnabled: false } });

		expect(screen.queryByTestId("agent-form-memory-extraction-enabled")).toBeNull();

		fireEvent.click(screen.getByTestId("agent-form-playbook-enabled"));

		expect(screen.getByTestId("agent-form-memory-extraction-enabled")).toBeTruthy();
	});

	it("round-trips memoryExtractionEnabled through the form (toggling to retrieval-only)", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "Helper", instructions: "Be helpful", playbookEnabled: true, memoryExtractionEnabled: true },
		});

		const toggle = screen.getByTestId("agent-form-memory-extraction-enabled") as HTMLInputElement;
		expect(toggle.checked).toBe(true);
		fireEvent.click(toggle);
		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ memoryExtractionEnabled: false }));
	});

	it("submits disableBaseScaffold true when the advanced toggle is switched on", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "Helper", instructions: "Be helpful" },
		});

		const toggle = screen.getByTestId("agent-form-disable-base-scaffold") as HTMLInputElement;
		expect(toggle.checked).toBe(false);
		fireEvent.click(toggle);
		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ disableBaseScaffold: true }));
	});

	it("submits disableToolRelevanceFilter true when the advanced toggle is switched on", () => {
		// The per-agent escape from the node's tool-relevance filter: off by default (follow the node setting), and the
		// form must round-trip it like any other advanced flag.
		const { onSubmit } = renderForm({
			initialValues: { name: "Helper", instructions: "Be helpful" },
		});

		const toggle = screen.getByTestId("agent-form-disable-tool-relevance-filter") as HTMLInputElement;
		expect(toggle.checked).toBe(false);
		fireEvent.click(toggle);
		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ disableToolRelevanceFilter: true }));
	});

	it("hides the orchestration section for a Single definition", () => {
		renderForm({ initialValues: { kind: "Single" } });

		expect(screen.queryByTestId("orchestration-topology-editor")).toBeNull();
	});

	it("shows the orchestration section only for an Orchestrator definition", () => {
		renderForm({ initialValues: { kind: "Orchestrator" } });

		expect(screen.getByTestId("orchestration-topology-editor")).toBeTruthy();
		// With no edges drawn the mesh-default hint is shown.
		expect(screen.getByTestId("orchestration-mesh-hint")).toBeTruthy();
	});

	it("blocks submit on an orchestrator with no specialists and surfaces the participants error", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "Coordinator", instructions: "Route work", kind: "Orchestrator" },
		});

		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText("An orchestrator needs at least one specialist participant.")).toBeTruthy();
	});

	it("submits a valid orchestrator carrying the selected participants", () => {
		const specialist = makeDefinition({ id: "22222222-2222-2222-2222-222222222222", name: "Researcher" });
		const { onSubmit } = renderForm({
			initialValues: {
				name: "Coordinator",
				instructions: "Route work",
				kind: "Orchestrator",
				orchestration: {
					participantAgentDefinitionIds: [specialist.id],
					handoffs: [],
					maxTurnsPerAgent: 8,
					returnToPrevious: false,
				},
			},
			allDefinitions: [specialist],
			selfId: "33333333-3333-3333-3333-333333333333",
		});

		fireEvent.click(screen.getByTestId("agent-form-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({
				kind: "Orchestrator",
				orchestration: expect.objectContaining({ participantAgentDefinitionIds: [specialist.id] }),
			}),
		);
	});

	it("warns that orchestration degrades when a participant model is not tool-capable", () => {
		const specialist = makeDefinition({
			id: "22222222-2222-2222-2222-222222222222",
			name: "Researcher",
			modelProfile: "llama3:8b",
		});
		renderForm({
			initialValues: {
				name: "Coordinator",
				instructions: "Route work",
				kind: "Orchestrator",
				modelProfile: "qwen3:8b",
				orchestration: {
					participantAgentDefinitionIds: [specialist.id],
					handoffs: [],
					maxTurnsPerAgent: 8,
					returnToPrevious: false,
				},
			},
			allDefinitions: [specialist],
			selfId: "33333333-3333-3333-3333-333333333333",
			toolCapableModels: ["qwen3:8b"],
		});

		expect(screen.getByTestId("orchestration-capability-warning")).toBeTruthy();
	});

	it("nudges toward an explicit route for a 2-agent mesh (triage + 1 specialist, no edges)", () => {
		const specialist = makeDefinition({ id: "22222222-2222-2222-2222-222222222222", name: "Researcher" });
		renderForm({
			initialValues: {
				name: "Coordinator",
				instructions: "Route work",
				kind: "Orchestrator",
				orchestration: {
					participantAgentDefinitionIds: [specialist.id],
					handoffs: [],
					maxTurnsPerAgent: 8,
					returnToPrevious: false,
				},
			},
			allDefinitions: [specialist],
			selfId: "33333333-3333-3333-3333-333333333333",
		});

		expect(screen.getByTestId("orchestration-two-agent-hint")).toBeTruthy();
	});

	it("does not show the 2-agent hint once there are two specialists (3+ agents)", () => {
		const specialistA = makeDefinition({ id: "22222222-2222-2222-2222-222222222222", name: "Researcher" });
		const specialistB = makeDefinition({ id: "44444444-4444-4444-4444-444444444444", name: "Writer" });
		renderForm({
			initialValues: {
				name: "Coordinator",
				instructions: "Route work",
				kind: "Orchestrator",
				orchestration: {
					participantAgentDefinitionIds: [specialistA.id, specialistB.id],
					handoffs: [],
					maxTurnsPerAgent: 8,
					returnToPrevious: false,
				},
			},
			allDefinitions: [specialistA, specialistB],
			selfId: "33333333-3333-3333-3333-333333333333",
		});

		// Mesh hint still shows (no edges), but the 2-agent nudge is gone.
		expect(screen.getByTestId("orchestration-mesh-hint")).toBeTruthy();
		expect(screen.queryByTestId("orchestration-two-agent-hint")).toBeNull();
	});
});
