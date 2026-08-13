// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KnowledgeRepositoryImportPanel } from "@/features/knowledge/components/KnowledgeRepositoryImportPanel";

function stubMatchMedia(): void {
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
}

describe("KnowledgeRepositoryImportPanel", () => {
	beforeEach(stubMatchMedia);
	afterEach(() => cleanup());

	it("submits the selected available repository by opaque folder id", () => {
		const onImport = vi.fn();
		render(
			<MantineProvider>
				<KnowledgeRepositoryImportPanel
					repositories={[
						{ id: "folder-1", alias: "xe-engine", availability: "Available" },
						{ id: "folder-2", alias: "offline", availability: "Unavailable" },
					]}
					isLoading={false}
					isImporting={false}
					onImport={onImport}
				/>
			</MantineProvider>,
		);

		const select = screen.getByTestId<HTMLInputElement>("knowledge-repository-select");
		fireEvent.click(select);
		fireEvent.click(screen.getByRole("option", { name: "xe-engine", hidden: true }));
		fireEvent.click(screen.getByTestId("knowledge-repository-import"));

		expect(onImport).toHaveBeenCalledTimes(1);
		expect(onImport).toHaveBeenCalledWith("folder-1");
	});

	it("cannot submit while no repository is selected", () => {
		const onImport = vi.fn();
		render(
			<MantineProvider>
				<KnowledgeRepositoryImportPanel repositories={[]} isLoading={false} isImporting={false} onImport={onImport} />
			</MantineProvider>,
		);

		const button = screen.getByTestId<HTMLButtonElement>("knowledge-repository-import");
		expect(button.disabled).toBe(true);
		fireEvent.click(button);
		expect(onImport).not.toHaveBeenCalled();
	});
});
