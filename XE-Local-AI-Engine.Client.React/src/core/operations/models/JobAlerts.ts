import type { Operation } from "@/core/operations/models/OperationModels";

// The alerts beyond the toast for an operation that finished while the user was elsewhere: a count in the tab title
// and, when the user turned it on and the browser allows it, an OS notification.

const TITLE_MARKER = /^\(\d+\) /u;

/** `title` with the unseen count in front, or without any count when `unseen` is 0. */
export function markedTitle(title: string, unseen: number): string {
	const base = title.replace(TITLE_MARKER, "");
	return unseen > 0 ? `(${unseen}) ${base}` : base;
}

/** True when the page the user is looking at is the operation's own page, which already shows the outcome. */
export function isOnOperationPage(operation: Operation, pathname: string): boolean {
	return pathname.replace(/\/+$/u, "") === operation.href.to;
}

/** Whether this browser can show OS notifications at all. */
export function systemNotificationsSupported(): boolean {
	return typeof Notification !== "undefined";
}

/** Shows an OS notification when the browser grants it; a click brings the tab back. Never throws. */
export function showSystemNotification(operation: Operation): void {
	if (!systemNotificationsSupported() || Notification.permission !== "granted") {
		return;
	}
	try {
		const notification = new Notification(operation.label, { body: operation.detail, tag: operation.id });
		notification.onclick = () => {
			globalThis.focus();
			notification.close();
		};
	} catch {
		// Some mobile browsers only notify through a service worker and throw on the constructor; the toast still shows.
	}
}
