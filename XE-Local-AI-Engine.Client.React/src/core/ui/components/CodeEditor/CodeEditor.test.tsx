// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const editorMock = vi.hoisted(() => {
	let value = "";
	let contentListener: (() => void) | undefined;
	const instance = {
		getValue: vi.fn(() => value),
		setValue: vi.fn((next: string) => {
			value = next;
			// Monaco emits onDidChangeModelContent for programmatic writes too.
			contentListener?.();
		}),
		getModel: vi.fn(() => ({ dispose: vi.fn() })),
		updateOptions: vi.fn(),
		onDidChangeModelContent: vi.fn((listener: () => void) => {
			contentListener = listener;
			return { dispose: vi.fn() };
		}),
		dispose: vi.fn(),
	};
	return {
		instance,
		create: vi.fn((_container: HTMLElement, options: { value: string }) => {
			value = options.value;
			return instance;
		}),
		setModelLanguage: vi.fn(),
		setTheme: vi.fn(),
		/** Simulates the user typing: mutates the model then fires the change listener, exactly as Monaco does. */
		type(next: string) {
			value = next;
			contentListener?.();
		},
	};
});

// The real module boots the ~3 MB Monaco runtime and needs a layout engine jsdom does not have; the wrapper's contract
// (create once, apply props in place, forward edits) is what this file pins.
vi.mock("@/core/ui/components/CodeEditor/MonacoRuntime", () => ({
	monaco: {
		editor: { create: editorMock.create, setModelLanguage: editorMock.setModelLanguage, setTheme: editorMock.setTheme },
	},
}));

describe("CodeEditor", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		installJsdomEnvironmentMocks();
	});
	afterEach(() => cleanup());

	it("lazily mounts Monaco with the given value, language and read-only state", async () => {
		renderWithMantine(<CodeEditor value="+added" language="diff" readOnly={true} data-testid="viewer" aria-label="Patch" />);

		expect(await screen.findByTestId("viewer")).toBeTruthy();
		expect(editorMock.create).toHaveBeenCalledTimes(1);
		expect(editorMock.create.mock.calls[0]?.[1]).toMatchObject({
			value: "+added",
			language: "diff",
			readOnly: true,
			domReadOnly: true,
			ariaLabel: "Patch",
		});
		expect(editorMock.setTheme).toHaveBeenCalledWith("xe-light");
	});

	it("applies value and language changes in place without recreating the editor", async () => {
		const { rerender } = renderWithMantine(<CodeEditor value="a" language="json" data-testid="viewer" />);
		await screen.findByTestId("viewer");

		rerender(
			<MantineProvider>
				<CodeEditor value="b" language="yaml" data-testid="viewer" />
			</MantineProvider>,
		);

		expect(editorMock.create).toHaveBeenCalledTimes(1);
		expect(editorMock.instance.setValue).toHaveBeenCalledWith("b");
		expect(editorMock.setModelLanguage).toHaveBeenLastCalledWith(expect.anything(), "yaml");
	});

	it("forwards edits through onChange and does not echo the same value back into the model", async () => {
		const onChange = vi.fn();
		const { rerender } = renderWithMantine(<CodeEditor value="a" onChange={onChange} data-testid="editor" />);
		await screen.findByTestId("editor");

		act(() => editorMock.type("ab"));
		expect(onChange).toHaveBeenCalledWith("ab");

		// The controlled round-trip: parent stores "ab" and re-renders with it. Monaco already holds "ab", so
		// setValue must NOT run (it would reset the caret and undo stack).
		editorMock.instance.setValue.mockClear();
		rerender(
			<MantineProvider>
				<CodeEditor value="ab" onChange={onChange} data-testid="editor" />
			</MantineProvider>,
		);
		expect(editorMock.instance.setValue).not.toHaveBeenCalled();
	});

	it("does not report a prop-driven value replacement as a user edit", async () => {
		const onChange = vi.fn();
		const { rerender } = renderWithMantine(<CodeEditor value="a" onChange={onChange} data-testid="editor" />);
		await screen.findByTestId("editor");

		rerender(
			<MantineProvider>
				<CodeEditor value="replaced by parent" onChange={onChange} data-testid="editor" />
			</MantineProvider>,
		);

		expect(editorMock.instance.setValue).toHaveBeenCalledWith("replaced by parent");
		expect(onChange).not.toHaveBeenCalled();

		// A real edit afterwards still reaches the parent.
		act(() => editorMock.type("replaced by parent!"));
		expect(onChange).toHaveBeenCalledWith("replaced by parent!");
	});

	it("disposes the editor on unmount", async () => {
		const { unmount } = renderWithMantine(<CodeEditor value="a" data-testid="viewer" />);
		await screen.findByTestId("viewer");
		unmount();
		expect(editorMock.instance.dispose).toHaveBeenCalledTimes(1);
	});
});
