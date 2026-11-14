// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { JobNotificationsSwitch } from "@/core/operations/components/JobNotificationsSwitch";
import { useJobNotificationStore } from "@/core/operations/stores/JobNotificationStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

const permission = vi.hoisted(() => ({
	current: "default" as NotificationPermission,
	answer: "granted" as NotificationPermission,
}));
const requestPermission = vi.fn(async () => {
	permission.current = permission.answer;
	return permission.answer;
});

beforeEach(() => {
	permission.current = "default";
	permission.answer = "granted";
	requestPermission.mockClear();
	vi.stubGlobal("Notification", {
		get permission() {
			return permission.current;
		},
		requestPermission,
	});
	useJobNotificationStore.getState().actions.setSystemNotifications(false);
});

afterEach(() => {
	cleanup();
	vi.unstubAllGlobals();
	localStorage.clear();
});

function toggle(): HTMLInputElement {
	return screen.getByTestId("job-notifications-switch") as HTMLInputElement;
}

describe("JobNotificationsSwitch", () => {
	it("asks the browser only when the user turns it on, and remembers a granted choice in this browser", async () => {
		renderWithProviders(<JobNotificationsSwitch />);
		expect(requestPermission).not.toHaveBeenCalled();

		fireEvent.click(toggle());

		await waitFor(() => expect(toggle().checked).toBe(true));
		expect(requestPermission).toHaveBeenCalledOnce();
		expect(localStorage.getItem("xe-job-system-notifications")).toBe("true");
	});

	it("stays off and says where to allow them when the browser refuses", async () => {
		permission.answer = "denied";
		renderWithProviders(<JobNotificationsSwitch />);

		fireEvent.click(toggle());

		await screen.findByText(/blocks notifications from this app/u);
		expect(toggle().checked).toBe(false);
		expect(useJobNotificationStore.getState().systemNotifications).toBe(false);
	});

	it("turns off without asking", () => {
		permission.current = "granted";
		useJobNotificationStore.getState().actions.setSystemNotifications(true);
		renderWithProviders(<JobNotificationsSwitch />);
		expect(toggle().checked).toBe(true);

		fireEvent.click(toggle());

		expect(toggle().checked).toBe(false);
		expect(requestPermission).not.toHaveBeenCalled();
	});

	it("is disabled where the browser has no notifications at all", () => {
		vi.stubGlobal("Notification", undefined);
		renderWithProviders(<JobNotificationsSwitch />);

		expect(toggle().disabled).toBe(true);
		expect(screen.getByText("This browser cannot show system notifications.")).toBeTruthy();
	});
});
