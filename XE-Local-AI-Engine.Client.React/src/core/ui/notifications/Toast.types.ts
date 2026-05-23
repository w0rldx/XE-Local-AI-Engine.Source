import type { ReactNode } from "react";

export interface ToastOptions {
	autoClose?: number | false;
	id?: string;
	title?: string;
}

export type ToastType = "success" | "error" | "info" | "warning";

export interface ToastConfig {
	color: "primary" | "secondary";
	icon: ReactNode;
}
