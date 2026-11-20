import { useSyncExternalStore } from "react";

interface WindowDimensions {
	readonly width: number;
	readonly height: number;
}

// One window "resize" listener for every consumer, attached while at least one is mounted.
const listeners = new Set<() => void>();
let snapshot: WindowDimensions = { width: globalThis.innerWidth, height: globalThis.innerHeight };

function notify() {
	for (const listener of listeners) {
		listener();
	}
}

function subscribe(listener: () => void) {
	if (listeners.size === 0) {
		window.addEventListener("resize", notify);
	}
	listeners.add(listener);
	return () => {
		listeners.delete(listener);
		if (listeners.size === 0) {
			window.removeEventListener("resize", notify);
		}
	};
}

// Read live, so a resize while nobody listened still shows; the object is kept while the size is unchanged, which is
// what useSyncExternalStore needs to avoid re-rendering forever.
function getSnapshot(): WindowDimensions {
	const { innerWidth: width, innerHeight: height } = globalThis;
	if (width !== snapshot.width || height !== snapshot.height) {
		snapshot = { width, height };
	}
	return snapshot;
}

export default function useWindowDimensions(): WindowDimensions {
	return useSyncExternalStore(subscribe, getSnapshot);
}
