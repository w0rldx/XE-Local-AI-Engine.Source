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

import { SkillForm, type SkillFormHandle } from "@/features/skills/components/SkillForm";
import type { SkillFormValues } from "@/features/skills/models/SkillModels";

const baseValues: SkillFormValues = {
	name: "",
	description: "",
	body: "",
	enabled: true,
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
		expect(screen.getByText(/lowercase letters, digits and dashes/i)).toBeTruthy();
	});

	it("rejects a name with uppercase letters", () => {
		const { onSubmit } = renderForm({ initialValues: { name: "BadName", description: "desc", body: "body text" } });

		fireEvent.click(screen.getByTestId("harness-save"));

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText(/lowercase letters, digits and dashes/i)).toBeTruthy();
	});

	it("submits trimmed values when name (with internal dashes), description and body are valid", () => {
		const { onSubmit } = renderForm({
			initialValues: { name: "invoice--review", description: "  How to review  ", body: "  # Body  " },
		});

		fireEvent.click(screen.getByTestId("harness-save"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({ name: "invoice--review", description: "How to review", body: "# Body" }),
		);
	});

	it("accepts typed input into the markdown body textarea", () => {
		renderForm({ initialValues: { name: "ok", description: "d" } });

		const body = screen.getByTestId("skill-form-body-textarea") as HTMLTextAreaElement;
		fireEvent.change(body, { target: { value: "fresh body" } });

		expect(body.value).toBe("fresh body");
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
