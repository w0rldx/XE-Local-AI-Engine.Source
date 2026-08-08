import type { ReactNode } from "react";

export interface ToastOptions {
	autoClose?: number | false;
	id?: string;
	title?: string;
}

// Options for the in-place progress toast (toast.progress). `id` is required because a progress toast is always an
// update-by-id of a sticky notification opened earlier with the same id; `percent`, when present, is rendered into the
// message so a single notification reflects live download progress without stacking new toasts.
export interface ToastProgressOptions {
	id: string;
	message: string;
	title?: string;
	percent?: number;
}

export type ToastType = "success" | "error" | "info" | "warning";

export interface ToastConfig {
	color: "primary" | "secondary";
	icon: ReactNode;
}
