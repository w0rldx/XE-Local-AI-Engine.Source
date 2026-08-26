// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { TrainingArtifactTextDialog } from "@/features/training/components/TrainingArtifactTextDialog";

vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }) }));
afterEach(cleanup);
beforeEach(() => {
	globalThis.ResizeObserver = class {
		disconnect = vi.fn();
		observe = vi.fn();
		unobserve = vi.fn();
	};
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
	});
});

describe("TrainingArtifactTextDialog", () => {
	it("preserves the promotion guidance and enables confirmation only after a name is supplied", () => {
		const confirm = vi.fn();
		const change = vi.fn();
		render(
			<MantineProvider>
				<TrainingArtifactTextDialog
					kind="promote"
					onChange={change}
					onClose={vi.fn()}
					onConfirm={confirm}
					opened={true}
					pending={false}
					value=""
				/>
			</MantineProvider>,
		);
		expect(screen.getByText(/quantization is appended/i)).toBeTruthy();
		expect((screen.getByRole("button", { name: "Register as model" }) as HTMLButtonElement).disabled).toBe(true);
		fireEvent.change(screen.getByLabelText("Model name"), { target: { value: "model-name" } });
		expect(change).toHaveBeenCalledWith("model-name");
	});
});
