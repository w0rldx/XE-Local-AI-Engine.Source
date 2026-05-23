import { IconAlertTriangle, IconCircleCheck, IconCircleX, IconInfoCircle } from "@tabler/icons-react";
import { notifications } from "@mantine/notifications";
import type { ToastConfig, ToastOptions, ToastType } from "@/core/ui/notifications/Toast.types";

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

function show(type: ToastType, message: string, options?: ToastOptions) {
	const { color, icon } = toastConfigByType[type];

	notifications.show({
		message,
		color,
		icon,
		autoClose: options?.autoClose,
		id: options?.id,
		title: options?.title,
	});
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
};
