// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";
import { ImportProgressPanel } from "@/features/models/components/ImportProgressPanel";
import { renderWithProviders } from "@/test/RenderWithProviders";

afterEach(cleanup);

function renderFailed(errorCode: string | null) {
	const status: GgufAcquisitionStatus = {
		operationId: "op-1",
		operationKind: "Import",
		modelName: "local-model",
		phase: "Failed",
		pct: undefined,
		completedBytes: null,
		totalBytes: null,
		startedAtUtc: null,
		updatedAtUtc: null,
		errorCode,
		sanitizedMessage: "Raw server text.",
	};
	renderWithProviders(<ImportProgressPanel operations={[status]} onCancel={() => undefined} cancellingOperationId={null} />);
	return screen.getByRole("alert");
}

describe("ImportProgressPanel failure text", () => {
	it("shows the translated text of a known code", () => {
		expect(renderFailed("SourceNotFound").textContent).toContain("The selected GGUF file was not found.");
	});

	it("falls back to the generic import sentence for an unknown code, never the server's text", () => {
		const alert = renderFailed("SomethingNew");

		expect(alert.textContent).toContain("The model could not be imported.");
		expect(alert.textContent).not.toContain("Raw server text.");
	});
});
