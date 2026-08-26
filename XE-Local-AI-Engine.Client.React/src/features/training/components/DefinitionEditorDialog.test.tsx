// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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

// The definition body is composed here but validated (and re-snapshotted) server-side, so the assertion that matters
// is the exact payload the SDK is handed — in particular that only tool NAMES are sent and the hold-out percentage is
// converted back to the fraction the contract stores.
const { createSpy, updateSpy } = vi.hoisted(() => ({ createSpy: vi.fn(), updateSpy: vi.fn() }));
vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	createTrainingDefinition: createSpy,
	updateTrainingDefinition: updateSpy,
}));

import type { TrainingDefinition } from "@/features/training/models/TrainingModels";
import { DefinitionEditorDialog } from "@/features/training/components/DefinitionEditorDialog";

const existing: TrainingDefinition = {
	id: "def-1",
	name: "Tool calls",
	teacherModelName: "teacher:Q4",
	teacherOutputMode: "Constrained",
	systemInstructions: "Answer with a tool call.",
	toolNames: ["get_time"],
	sampleKinds: [{ kind: "single-tool-call", count: 12, label: "Good" }],
	holdoutFraction: 0.2,
	temperature: 0.5,
	baseSeed: "42",
	criticEnabled: false,
	criticModelName: null,
	definitionVersion: 3,
	version: 7,
	updatedAtUtc: 1,
};

function renderDialog(definition: TrainingDefinition | null) {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false }, queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<DefinitionEditorDialog definition={definition} onClose={vi.fn()} opened={true} />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

const submitButton = (): HTMLButtonElement => screen.getByTestId("training-definition-submit") as HTMLButtonElement;
const inputValue = (testId: string): string => (screen.getByTestId(testId) as HTMLInputElement).value;

describe("DefinitionEditorDialog", () => {
	beforeEach(() => {
		createSpy.mockResolvedValue({ data: { ...existing, body: {} } });
		updateSpy.mockResolvedValue({ data: { ...existing, body: {} } });
		// Mantine reads the color scheme from matchMedia; jsdom doesn't implement it.
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
		// DialogShell's ScrollArea (Mantine) uses a ResizeObserver; jsdom doesn't implement it.
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("refuses to submit an empty form and names the first missing field", () => {
		renderDialog(null);

		expect(screen.getByTestId("training-definition-error").textContent).toBe("A definition needs a name.");
		expect(submitButton().disabled).toBe(true);
	});

	it("refuses a hold-out share outside the contract's 5–30 percent band", () => {
		renderDialog(existing);

		fireEvent.change(screen.getByTestId("training-definition-holdout"), { target: { value: "45" } });

		expect(screen.getByTestId("training-definition-error").textContent).toBe("The hold-out share must be between 5 and 30 percent.");
		expect(submitButton().disabled).toBe(true);
	});

	it("refuses a base seed that is not a whole number", () => {
		renderDialog(existing);

		fireEvent.change(screen.getByTestId("training-definition-seed"), { target: { value: "1.5" } });

		expect(screen.getByTestId("training-definition-error").textContent).toBe("The base seed must be a whole number.");
	});

	it("prefills from the edited definition and updates it with its expected version", async () => {
		renderDialog(existing);

		expect(inputValue("training-definition-name")).toBe("Tool calls");
		expect(inputValue("training-definition-holdout")).toBe("20");

		fireEvent.click(screen.getByTestId("training-definition-submit"));

		await waitFor(() => expect(updateSpy).toHaveBeenCalledTimes(1));
		const call = updateSpy.mock.calls[0]?.[0];
		expect(call?.path).toEqual({ definitionId: "def-1" });
		expect(call?.body?.expectedVersion).toBe(7);
		expect(call?.body?.name).toBe("Tool calls");
		expect(call?.body?.body).toEqual({
			teacherModelName: "teacher:Q4",
			teacherOutputMode: "Constrained",
			systemInstructions: "Answer with a tool call.",
			// Only the name is sent: the server re-snapshots description, schema and approval from the live catalog.
			tools: [{ name: "get_time" }],
			sampleKinds: [{ kind: "single-tool-call", count: 12, label: "Good" }],
			holdoutFraction: 0.2,
			temperature: 0.5,
			baseSeed: "42",
			criticEnabled: false,
			criticModelName: null,
		});
		expect(createSpy).not.toHaveBeenCalled();
	});
});

// DialogShell goes full-screen below 768px, leaving a sample-kind row under 360px wide — narrower than the kind,
// count, label and delete controls need side by side. The row must be free to wrap rather than overflow.
describe("DefinitionEditorDialog narrow layout", () => {
	it("lets a sample-kind row wrap, with the kind free to shrink", () => {
		renderDialog(null);

		const row = screen.getAllByTestId("training-definition-kind-row")[0] as HTMLElement;
		expect(row.style.getPropertyValue("--group-wrap")).toBe("wrap");
		const kindRoot = screen.getByLabelText("Kind").closest(".mantine-InputWrapper-root") as HTMLElement;
		expect(kindRoot.style.minWidth).toBe("0rem");
	});
});
