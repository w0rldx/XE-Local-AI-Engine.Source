// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DevelopmentProjectForm } from "@/features/development/components/DevelopmentProjectForm";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

function installDomMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
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

describe("DevelopmentProjectForm", () => {
	beforeEach(() => {
		installDomMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("requires an available registered repository and explicit host-user trust", async () => {
		const submit = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[
						{ id: "available", alias: "Workspace", availability: "Available" },
						{ id: "unavailable", alias: "Moved", availability: "Unavailable" },
					]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={vi.fn()}
					onSubmit={submit}
				/>
			</MantineProvider>,
		);

		const create = screen.getByTestId("development-create-project") as HTMLButtonElement;
		expect(create.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-repository-select"));
		fireEvent.click(await screen.findByText("Workspace"));
		expect(create.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-trust-acknowledgement"));
		expect(create.disabled).toBe(false);
		expect(screen.getByText(/not OS isolation/)).toBeTruthy();
	});

	it("registers an absolute host path once through the shared dialog", async () => {
		const register = vi.fn().mockResolvedValue({ id: "repository-2", alias: "Engine", availability: "Available" });
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={register}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-register-repository"));
		fireEvent.change(await screen.findByTestId("development-register-alias"), { target: { value: "Engine" } });
		fireEvent.change(screen.getByTestId("development-register-path"), {
			target: { value: "/home/operator/projects/engine" },
		});
		fireEvent.click(screen.getByTestId("development-register-repository"));

		await waitFor(() => expect(register).toHaveBeenCalledWith({ alias: "Engine", hostPath: "/home/operator/projects/engine" }));
	});

	it("keeps the registration dialog open and surfaces contract failures", async () => {
		const register = vi.fn().mockRejectedValue(new Error("The repository registration response was incomplete."));
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={register}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-register-repository"));
		fireEvent.change(await screen.findByTestId("development-register-alias"), { target: { value: "Engine" } });
		fireEvent.change(screen.getByTestId("development-register-path"), {
			target: { value: "/home/operator/projects/engine" },
		});
		fireEvent.click(screen.getByTestId("development-register-repository"));

		expect(await screen.findByText("The repository registration response was incomplete.")).toBeTruthy();
		expect(screen.getByRole("dialog")).toBeTruthy();
	});
});
