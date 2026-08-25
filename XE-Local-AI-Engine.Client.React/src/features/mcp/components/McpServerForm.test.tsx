// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { McpServerForm } from "@/features/mcp/components/McpServerForm";
import { type McpServerFormValues, maskedEnvValue } from "@/features/mcp/models/McpServerModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

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
		env: [{ key: "TOKEN", value: maskedEnvValue }],
		url: "",
		trustTier: "Sandboxed",
	};

	render(
		<MantineProvider>
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
		expect(onSubmit.mock.lastCall?.[0].env).toEqual([{ key: "TOKEN", value: maskedEnvValue }]);
	});

	it("submits a retyped value instead of the sentinel", () => {
		const onSubmit = vi.fn();
		const value = renderForm(onSubmit);

		fireEvent.change(value, { target: { value: "rotated" } });
		fireEvent.click(screen.getByTestId("mcp-form-submit"));

		expect(onSubmit.mock.lastCall?.[0].env).toEqual([{ key: "TOKEN", value: "rotated" }]);
	});
});
