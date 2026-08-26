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

import { SkillForm, type SkillFormHandle } from "@/features/skills/components/SkillForm";
import type { SkillFormValues } from "@/features/skills/models/SkillModels";

const baseValues: SkillFormValues = {
	allowedTools: "",
	body: "",
	compatibility: "",
	description: "",
	enabled: true,
	generated: false,
	generationMetadata: null,
	license: "",
	metadata: null,
	name: "",
};

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

interface HarnessProps {
	initialValues?: Partial<SkillFormValues>;
	showEnabledToggle?: boolean;
	onSubmit?: (values: SkillFormValues) => void;
}

// The form's Save is driven imperatively via the handle (the dialog footer does the same in SkillsPage). Mirror that
// with a ref + a test-only Save button so the validate-then-submit path is exercised.
function renderForm(props: HarnessProps = {}) {
	const onSubmit = props.onSubmit ?? vi.fn();

	function FormHarness() {
		const ref = useRef<SkillFormHandle>(null);
		return (
			<>
				<SkillForm
					ref={ref}
					initialValues={{ ...baseValues, ...props.initialValues }}
					isSubmitting={false}
					showEnabledToggle={props.showEnabledToggle ?? false}
					onSubmit={onSubmit}
					onCancel={vi.fn()}
					hideActions={true}
				/>
				<button type="button" data-testid="harness-save" onClick={() => ref.current?.submit()}>
					save
				</button>
			</>
		);
	}

	renderWithProviders(<FormHarness />);
	return { onSubmit };
}

describe("SkillForm", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the privacy note that skills are sent to the agent's model", () => {
		renderForm();

		expect(screen.getByTestId("skill-form-privacy-note").textContent).toContain("sent to the agent's model");
	});

	it("blocks submit and shows required errors when name, description and body are empty", () => {
		const { onSubmit } = renderForm();

		fireEvent.click(screen.getByTestId("harness-save"));

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText("Name is required.")).toBeTruthy();
		expect(screen.getByText("Description is required.")).toBeTruthy();
		expect(screen.getByText("Body is required.")).toBeTruthy();
	});

	it("rejects a name that violates the MAF-safe pattern (leading dash) with the invalid-name message", () => {
		const { onSubmit } = renderForm({ initialValues: { name: "-bad", description: "desc", body: "body text" } });

		fireEvent.click(screen.getByTestId("harness-save"));

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText(/lowercase letters and digits separated by single dashes/i)).toBeTruthy();
	});

	it("rejects a name with uppercase letters", () => {
		const { onSubmit } = renderForm({ initialValues: { name: "BadName", description: "desc", body: "body text" } });

		fireEvent.click(screen.getByTestId("harness-save"));

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText(/lowercase letters and digits separated by single dashes/i)).toBeTruthy();
	});

	it("submits trimmed values when name (with single internal dashes), description and body are valid", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "invoice-review", description: "  How to review  ", body: "  # Body  " },
		});

		fireEvent.click(screen.getByTestId("harness-save"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({ name: "invoice-review", description: "How to review", body: "# Body" }),
		);
	});

	it("accepts typed input into the markdown body textarea", () => {
		renderForm({ initialValues: { name: "ok", description: "d" } });

		const body = screen.getByTestId("skill-form-body-textarea") as HTMLTextAreaElement;
		fireEvent.change(body, { target: { value: "fresh body" } });

		expect(body.value).toBe("fresh body");
	});

	it("words allowed-tools as display-only, because this node enforces nothing from it", () => {
		renderForm();

		expect(screen.getByText(/neither grants nor restricts any tool/i)).toBeTruthy();
	});

	it("hints the body length against the specification's guidance once it is exceeded", () => {
		renderForm({ initialValues: { body: "x\n".repeat(600) } });

		const budget = screen.getByTestId("skill-form-body-budget");
		expect(budget.textContent).toContain("601 lines");
		expect(budget.textContent).toContain("staying under 500 lines");
	});

	it("keeps the body hint neutral while the body is within guidance", () => {
		renderForm({ initialValues: { body: "short body" } });

		expect(screen.getByTestId("skill-form-body-budget").textContent).not.toContain("staying under");
	});

	it("says an imported skill's instructions run with the agent's tool access", () => {
		render(
			<MantineProvider>
				<SkillForm
					initialValues={baseValues}
					isSubmitting={false}
					showEnabledToggle={true}
					onSubmit={vi.fn()}
					onCancel={vi.fn()}
					hideActions={true}
					provenance={{ importedAtUtc: 1, origin: "Imported", sourceUri: "github:microsoft/skills" }}
				/>
			</MantineProvider>,
		);

		const note = screen.getByTestId("skill-form-imported-note").textContent ?? "";
		expect(note).toContain("github:microsoft/skills");
		expect(note).toContain("tool access");
	});

	it("hides the enabled toggle on create and shows it on edit", () => {
		const { rerender } = render(
			<MantineProvider>
				<SkillForm
					initialValues={baseValues}
					isSubmitting={false}
					showEnabledToggle={false}
					onSubmit={vi.fn()}
					onCancel={vi.fn()}
					hideActions={true}
				/>
			</MantineProvider>,
		);
		expect(screen.queryByTestId("skill-form-enabled")).toBeNull();

		rerender(
			<MantineProvider>
				<SkillForm
					initialValues={baseValues}
					isSubmitting={false}
					showEnabledToggle={true}
					onSubmit={vi.fn()}
					onCancel={vi.fn()}
					hideActions={true}
				/>
			</MantineProvider>,
		);
		expect(screen.getByTestId("skill-form-enabled")).toBeTruthy();
	});
});

// Frontmatter metadata is free text of unknown length carried through from the imported skill. Rendered as a bare
// two-column table it widened the dialog on a phone; the scroll container gives the pair its own horizontal overflow
// and the value cell breaks inside a long unspaced token.
describe("SkillForm metadata table", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("wraps the metadata table in a scroll container with a minimum width", () => {
		renderForm({ initialValues: { metadata: { source: "https://example.test/a-very-long-unspaced-metadata-value-that-cannot-wrap" } } });

		const container = screen.getByTestId("skill-form-metadata-scroll");

		expect(container.querySelector("table")).toBeTruthy();
		expect(container.style.getPropertyValue("--table-min-width")).toContain("20rem");
	});

	it("breaks a long metadata value instead of letting it push the row wider", () => {
		const value = "https://example.test/a-very-long-unspaced-metadata-value-that-cannot-wrap";
		renderForm({ initialValues: { metadata: { source: value } } });

		const cell = screen.getByText(value);

		expect(cell.style.wordBreak).toBe("break-word");
	});
});
