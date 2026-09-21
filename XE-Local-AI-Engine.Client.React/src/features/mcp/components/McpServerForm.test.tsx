// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { McpServerForm } from "@/features/mcp/components/McpServerForm";
import { type McpServerFormValues, maskedEnvValue } from "@/features/mcp/models/McpServerModels";
import { testMantineTheme } from "@/test/MantineTestRender";

// react-i18next is deliberately NOT mocked: the suite initialises `src/i18n.ts`, so `t()` resolves against the shipped
// `en` bundle and interpolates. A stub returning the in-code default verbatim would assert "{{index}}" at a user.

afterEach(cleanup);

// Mantine reads matchMedia on mount; jsdom has none.
beforeEach(() => {
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
	// Mantine's autosizing Textarea listens on document.fonts, which jsdom does not provide.
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
});

function renderForm(onSubmit: (values: McpServerFormValues) => void) {
	const initialValues: McpServerFormValues = {
		name: "Filesystem tools",
		description: "",
		transportKind: "Stdio",
		command: "/usr/bin/fs-mcp",
		arguments: [],
		workingDirectory: "",
		// Two rows: identically-shaped rows are exactly what the numbered accessible names have to tell apart.
		env: [
			{ key: "TOKEN", value: maskedEnvValue },
			{ key: "REGION", value: "eu" },
		],
		url: "",
		trustTier: "Sandboxed",
	};

	render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<McpServerForm initialValues={initialValues} isSubmitting={false} onSubmit={onSubmit} onCancel={vi.fn()} />
		</MantineProvider>,
	);

	return screen.getByTestId("mcp-form-env-value-0") as HTMLInputElement;
}

describe("McpServerForm masked env values", () => {
	// The sentinel is a wire protocol token, not something to show a human. It used to render literally in the box.
	it("renders a stored value as an empty box with the unchanged placeholder", () => {
		const value = renderForm(vi.fn());

		expect(value.value).toBe("");
		expect(value.placeholder).toBe("unchanged — enter a new value to replace");
	});

	// The half that matters: clearing the box must not blank the stored secret. Removal is the trash button.
	it("submits the sentinel back when an existing value is left empty", () => {
		const onSubmit = vi.fn();
		renderForm(onSubmit);

		fireEvent.click(screen.getByTestId("mcp-form-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		expect(onSubmit.mock.lastCall?.[0].env).toEqual([
			{ key: "TOKEN", value: maskedEnvValue },
			{ key: "REGION", value: "eu" },
		]);
	});

	it("submits a retyped value instead of the sentinel", () => {
		const onSubmit = vi.fn();
		const value = renderForm(onSubmit);

		fireEvent.change(value, { target: { value: "rotated" } });
		fireEvent.click(screen.getByTestId("mcp-form-submit"));

		expect(onSubmit.mock.lastCall?.[0].env).toEqual([
			{ key: "TOKEN", value: "rotated" },
			{ key: "REGION", value: "eu" },
		]);
	});
});

// KEY, VALUE and the remove button do not fit the ~358px body of a full-screen dialog on a phone. Two <input>s cannot
// shrink below their intrinsic width, so a nowrap row pushed the trash button off-screen instead of absorbing it.
describe("McpServerForm env row layout", () => {
	it("lets an env row wrap instead of forcing one line", () => {
		renderForm(vi.fn());

		const row = screen.getByTestId("mcp-form-env-row-0");

		expect(row.style.getPropertyValue("--group-wrap")).not.toBe("nowrap");
	});

	it("gives both env inputs a flex basis so the row breaks before it squeezes them", () => {
		renderForm(vi.fn());

		const row = screen.getByTestId("mcp-form-env-row-0");
		const [key, value] = Array.from(row.querySelectorAll<HTMLElement>(".mantine-TextInput-root"));

		expect(key?.style.flexBasis).toBe("140px");
		expect(value?.style.flexBasis).toBe("200px");
	});

	// Both boxes were named only by their placeholder, and the value box's placeholder is a sentence about the row's
	// masked state — neither reaches a screen reader as a name. A name shared by every row is no better: the number
	// is what makes row 2 reachable.
	it("numbers both env boxes per row, masked row included", () => {
		renderForm(vi.fn());

		expect(screen.getByRole("textbox", { name: "Variable 1 key" })).toBe(screen.getByTestId("mcp-form-env-key-0"));
		expect(screen.getByRole("textbox", { name: "Variable 1 value" })).toBe(screen.getByTestId("mcp-form-env-value-0"));
		expect(screen.getByRole("textbox", { name: "Variable 2 key" })).toBe(screen.getByTestId("mcp-form-env-key-1"));
		expect(screen.getByRole("textbox", { name: "Variable 2 value" })).toBe(screen.getByTestId("mcp-form-env-value-1"));
		expect(screen.getByRole("button", { name: "Remove variable 2" })).toBeTruthy();
	});
});
