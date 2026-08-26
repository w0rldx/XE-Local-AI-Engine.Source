// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { PartRow } from "@/features/images/components/ImageDownloadPartRow";
import type { PartDraft } from "@/features/images/models/ImageModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The row is pure presentation; what is worth pinning is that it can break onto a second line. A role Select, a file
// TextInput and a remove button do not fit the ~358px body of a full-screen dialog on a phone, and a nowrap row has no
// way to absorb that — an <input> refuses to shrink below its intrinsic width, so the button gets pushed off-screen.

function part(overrides: Partial<PartDraft> = {}): PartDraft {
	return {
		id: "part-1",
		role: "Diffusion",
		fileName: "",
		repoId: "",
		sizeBytes: "",
		sha256: "",
		...overrides,
	};
}

function renderRow() {
	return renderWithProviders(<PartRow part={part()} index={0} canRemove={true} onChange={vi.fn()} onRemove={vi.fn()} />);
}

describe("PartRow", () => {
	afterEach(cleanup);

	it("lets the role/file row wrap instead of forcing one line", () => {
		renderRow();

		const row = screen.getByTestId("image-model-download-part-row-0");

		expect(row.style.getPropertyValue("--group-wrap")).not.toBe("nowrap");
	});

	it("gives the role and file controls a flex basis so the row breaks before it squeezes them", () => {
		renderRow();

		const row = screen.getByTestId("image-model-download-part-row-0");
		const role = row.querySelector<HTMLElement>(".mantine-Select-root");
		const file = row.querySelector<HTMLElement>(".mantine-TextInput-root");

		expect(role?.style.flexBasis).toBe("150px");
		expect(file?.style.flexBasis).toBe("200px");
	});
});
