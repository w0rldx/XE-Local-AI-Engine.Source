import { IconAlertTriangle, IconCircleCheck, IconCircleX, IconInfoCircle } from "@tabler/icons-react";
import { notifications } from "@mantine/notifications";
import { t } from "i18next";
import type { ToastConfig, ToastOptions, ToastProgressOptions, ToastType } from "@/core/ui/notifications/Toast.types";

// Default auto-close duration (ms) for finalized toasts. Mirrors the ThemeProvider's <Notifications autoClose={5000} />
// so a toast.success/error finalizing a sticky progress toast re-arms the dismiss timer instead of inheriting the
// progress toast's autoClose:false (which left it stuck on screen, dismissable only by manual swipe).
const DEFAULT_AUTO_CLOSE_MS = 5000;

const toastConfigByType: Record<ToastType, ToastConfig> = {
	success: {
		color: "primary",
		icon: <IconCircleCheck size={16} />,
	},
	info: {
		color: "primary",
		icon: <IconInfoCircle size={16} />,
	},
	error: {
		color: "secondary",
		icon: <IconCircleX size={16} />,
	},
	warning: {
		color: "secondary",
		icon: <IconAlertTriangle size={16} />,
	},
};

// Last-ditch guard against a toast with nothing in it. A caller that derives its message from a server failure can
// legitimately end up with "" — ApiError resolves its message to an empty string when the response carried no detail,
// title or domain message — and Mantine happily renders the coloured card, the icon and the dismiss button around no
// text at all. A signal that says nothing is worse than the generic sentence, so substitute it. A toast that carries a
// title is left alone: the title already says something, and a body appended under it would be noise.
function resolveMessage(type: ToastType, message: string, title?: string): string {
	if (message.trim().length > 0 || (title !== undefined && title.trim().length > 0)) {
		return message;
	}

	return type === "success" || type === "info"
		? t("notifications.emptyMessage", "Done.")
		: t("errorMessages.defaultErrorMessage", "An error occurred");
}

function show(type: ToastType, message: string, options?: ToastOptions) {
	const { color, icon } = toastConfigByType[type];

	// Open-or-update by id (mirrors `progress` below): `notifications.show` opens a fresh toast, and is a no-op when
	// a toast with this id already exists — e.g. the sticky loading toast left by `toast.progress`. `notifications.update`
	// then patches that existing toast, explicitly clearing `loading`, restoring the close button, and re-arming
	// `autoClose`, so a progress→success/error finalize turns into an auto-dismissing toast instead of staying stuck on
	// screen. When no toast with this id exists, `update` is a no-op and the values from `show` stand.
	const payload = {
		message: resolveMessage(type, message, options?.title),
		color,
		icon,
		loading: false,
		withCloseButton: true,
		autoClose: options?.autoClose ?? DEFAULT_AUTO_CLOSE_MS,
		id: options?.id,
		title: options?.title,
	};
	notifications.show(payload);
	notifications.update(payload);
}

// Composes the message line for a progress toast: when a percent is known the message is suffixed with a rounded
// "— NN%" so a single in-place notification reflects live progress; without a percent the message stands alone.
function progressMessage(message: string, percent?: number): string {
	if (percent === undefined || !Number.isFinite(percent)) {
		return message;
	}
	return `${message} — ${Math.round(percent)}%`;
}

export const toast = {
	success(message: string, options?: ToastOptions) {
		show("success", message, options);
	},
	error(message: string, options?: ToastOptions) {
		show("error", message, options);
	},
	info(message: string, options?: ToastOptions) {
		show("info", message, options);
	},
	warn(message: string, options?: ToastOptions) {
		show("warning", message, options);
	},
	warning(message: string, options?: ToastOptions) {
		show("warning", message, options);
	},
	// In-place progress toast keyed by `id`. Idempotent open-or-update: `notifications.show` opens a sticky loading
	// toast on the first call (and is a no-op if one with this id already exists), then `notifications.update` patches
	// the message/percent in place on every subsequent call so a long download surfaces as ONE animating notification
	// rather than a stack. Finalize by calling toast.success / toast.error with the SAME id (the finalize toast turns
	// off the loading spinner and re-applies autoClose).
	progress(options: ToastProgressOptions) {
		const message = progressMessage(options.message, options.percent);
		const payload = {
			id: options.id,
			title: options.title,
			message,
			loading: true,
			autoClose: false as const,
			withCloseButton: false,
		};
		notifications.show(payload);
		notifications.update(payload);
	},
};
