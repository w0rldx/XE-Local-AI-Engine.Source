import { type DragEvent, useState } from "react";

export interface PaneFileDrop {
	/** Whether a file is currently being dragged over the target — drives the drop overlay. */
	isFileDragActive: boolean;
	/** Drag handlers to spread onto the drop target element. Inert (no-op) when the zone is disabled. */
	dropProps: {
		onDragOver: (event: DragEvent<HTMLElement>) => void;
		onDragLeave: (event: DragEvent<HTMLElement>) => void;
		onDrop: (event: DragEvent<HTMLElement>) => void;
	};
}

/**
 * Container-level drag-and-drop file upload (e.g. the chat pane). Returns the active flag for an overlay plus the
 * handlers to spread onto the drop target. When `enabled` is false the handlers no-op, so a disabled composer
 * (mid-send or capability off) never highlights or uploads. Only file drags trigger the overlay (text/selection
 * drags are ignored), and a leave that stays within the target's subtree is ignored to avoid overlay flicker.
 */
export function usePaneFileDrop(enabled: boolean, onFiles: (files: File[]) => void): PaneFileDrop {
	const [isFileDragActive, setFileDragActive] = useState(false);

	const onDragOver = (event: DragEvent<HTMLElement>): void => {
		if (!enabled || !event.dataTransfer.types.includes("Files")) {
			return;
		}
		event.preventDefault();
		setFileDragActive(true);
	};

	const onDragLeave = (event: DragEvent<HTMLElement>): void => {
		if (event.currentTarget.contains(event.relatedTarget as Node | null)) {
			return;
		}
		setFileDragActive(false);
	};

	const onDrop = (event: DragEvent<HTMLElement>): void => {
		if (!enabled) {
			return;
		}
		event.preventDefault();
		setFileDragActive(false);
		const files = Array.from(event.dataTransfer.files);
		if (files.length > 0) {
			onFiles(files);
		}
	};

	return { isFileDragActive, dropProps: { onDragOver, onDragLeave, onDrop } };
}
