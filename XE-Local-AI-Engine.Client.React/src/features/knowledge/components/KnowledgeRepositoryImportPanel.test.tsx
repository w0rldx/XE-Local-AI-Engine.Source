// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { KnowledgeRepositoryImportPanel } from "@/features/knowledge/components/KnowledgeRepositoryImportPanel";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("KnowledgeRepositoryImportPanel", () => {
	afterEach(() => cleanup());

	it("submits the selected available repository by opaque folder id", () => {
		const onImport = vi.fn();
		renderWithProviders(
			<KnowledgeRepositoryImportPanel
				repositories={[
					{ id: "folder-1", alias: "xe-engine", availability: "Available" },
					{ id: "folder-2", alias: "offline", availability: "Unavailable" },
				]}
				isLoading={false}
				isImporting={false}
				onImport={onImport}
			/>,
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
		renderWithProviders(
			<KnowledgeRepositoryImportPanel repositories={[]} isLoading={false} isImporting={false} onImport={onImport} />,
		);

		const button = screen.getByTestId<HTMLButtonElement>("knowledge-repository-import");
		expect(button.disabled).toBe(true);
		fireEvent.click(button);
		expect(onImport).not.toHaveBeenCalled();
	});
});
