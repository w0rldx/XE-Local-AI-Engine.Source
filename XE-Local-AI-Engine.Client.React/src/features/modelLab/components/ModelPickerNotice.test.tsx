// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("ModelPickerNotice", () => {
	afterEach(cleanup);

	it("links an empty model list to the Models page", async () => {
		renderWithProviders(<ModelPickerNotice status="success" modelCount={0} />, { withRouter: true });

		const link = await screen.findByTestId("model-lab-no-models-link");
		expect(link.getAttribute("href")).toBe("/models");
		expect(link.textContent).toBe("Open Models");
	});

	it("offers no Models link when the list failed to load", async () => {
		renderWithProviders(<ModelPickerNotice status="error" modelCount={0} />, { withRouter: true });

		expect(await screen.findByTestId("model-lab-models-failed")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-no-models-link")).toBeNull();
	});
});
