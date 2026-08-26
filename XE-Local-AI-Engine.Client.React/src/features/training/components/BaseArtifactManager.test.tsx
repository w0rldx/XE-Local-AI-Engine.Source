// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { BaseArtifactView } from "@/features/training/models/TrainingModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

const mocks = vi.hoisted(() => ({ artifacts: [] as BaseArtifactView[], noop: vi.fn() }));

vi.mock("@/features/training/queries/useTrainingQueries", () => ({
	useBaseArtifacts: () => ({ data: mocks.artifacts }),
	useCreateBaseArtifact: () => ({ isPending: false, mutate: mocks.noop }),
	useDeleteBaseArtifact: () => ({ isPending: false, mutate: mocks.noop }),
	useCancelBaseArtifact: () => ({ isPending: false, mutate: mocks.noop }),
}));

import { BaseArtifactManager } from "@/features/training/components/BaseArtifactManager";

function artifact(overrides: Partial<BaseArtifactView> = {}): BaseArtifactView {
	return {
		id: "artifact-1",
		repoId: "unsloth/Llama-3.2-1B-Instruct",
		revision: "main",
		status: "Ready",
		totalBytes: 2_400_000_000,
		files: [],
		license: null,
		errorMessage: null,
		progress: null,
		...overrides,
	};
}

function renderManager(artifacts: BaseArtifactView[]) {
	mocks.artifacts = artifacts;
	return renderWithMantine(<BaseArtifactManager />);
}

// Five columns — repository, status, license, size, actions — do not fit a phone. The table therefore has to carry
// its own horizontal scroll, or it widens the page and every other section scrolls sideways with it.
describe("BaseArtifactManager", () => {
	beforeEach(installJsdomEnvironmentMocks);
	afterEach(cleanup);

	it("keeps the checkpoint table inside its own scroll container", () => {
		renderManager([artifact()]);

		const table = screen.getByTestId("training-base-artifacts-table");
		expect(table.closest(".mantine-TableScrollContainer-scrollContainer")).not.toBeNull();
	});

	it("renders the empty state rather than an empty table when nothing has been downloaded", () => {
		renderManager([]);

		expect(screen.queryByTestId("training-base-artifacts-table")).toBeNull();
	});
});
