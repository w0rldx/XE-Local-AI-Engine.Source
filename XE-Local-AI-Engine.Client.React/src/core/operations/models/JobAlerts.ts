import type { Operation } from "@/core/operations/models/OperationModels";

// The alerts beyond the toast for an operation that finished while the user was elsewhere: a count in the tab title
// and, when the user turned it on and the browser allows it, an OS notification.

const TITLE_MARKER = /^\(\d+\) /u;

/** `title` with the unseen count in front, or without any count when `unseen` is 0. */
export function markedTitle(title: string, unseen: number): string {
	const base = title.replace(TITLE_MARKER, "");
	return unseen > 0 ? `(${unseen}) ${base}` : base;
}

/**
 * True when the page the user is looking at is the operation's own page, which already shows the outcome. A graph run's
 * page is that run's view: the editor or another run's view is a different page.
 */
export function isOnOperationPage(
	operation: Operation,
	location: { readonly pathname: string; readonly search: string },
): boolean {
	const { href } = operation;
	const page = "params" in href ? href.to.replace("$sessionId", encodeURIComponent(href.params.sessionId)) : href.to;
	if (location.pathname.replace(/\/+$/u, "") !== page) {
		return false;
	}
	return !("search" in href && "runId" in href.search) || new URLSearchParams(location.search).get("runId") === href.search.runId;
}

// Waits already alerted in this browser, oldest first, so a reload or another tab does not alert the same wait again.
// A wait seen leaving (decided, answered, gone) is forgotten, so the next wait of the same run or session alerts. Every
// id still waiting is kept; only waits that ended while no page watched them are capped.
const ALERTED_WAITS_KEY = "xe-alerted-waits";
const ENDED_WAITS_KEPT = 50;

/** The wait ids already alerted. Storage that is unavailable or holds garbage reads as none. */
export function alertedWaits(): string[] {
	try {
		const stored: unknown = JSON.parse(globalThis.localStorage?.getItem(ALERTED_WAITS_KEY) ?? "[]");
		return Array.isArray(stored) ? stored.filter((id): id is string => typeof id === "string") : [];
	} catch {
		return [];
	}
}

/** Adds the waits just alerted, drops the ones seen leaving, and caps the ids no longer waiting. */
export function recordAlertedWaits(
	alerted: readonly string[],
	claimed: readonly string[],
	released: readonly string[],
	waitingNow: ReadonlySet<string>,
): void {
	const kept = [...alerted.filter((id) => !released.includes(id) && !claimed.includes(id)), ...claimed];
	const ended = kept.filter((id) => !waitingNow.has(id));
	const evicted = new Set(ended.slice(0, Math.max(0, ended.length - ENDED_WAITS_KEPT)));
	try {
		globalThis.localStorage?.setItem(ALERTED_WAITS_KEY, JSON.stringify(kept.filter((id) => !evicted.has(id))));
	} catch {
		// Unavailable storage or quota: the coordinator's snapshot map still keeps refetches from alerting twice.
	}
}

/** Forgets every alerted wait; on logout only, so the next session alerts what still waits. */
export function clearAlertedWaits(): void {
	try {
		globalThis.localStorage?.removeItem(ALERTED_WAITS_KEY);
	} catch {
		// Unavailable storage holds nothing to forget.
	}
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
