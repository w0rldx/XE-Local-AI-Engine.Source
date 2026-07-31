export function installDevelopmentPerformanceFeedback(): () => void {
	if (!("PerformanceObserver" in window)) {
		return () => undefined;
	}

	const observers: PerformanceObserver[] = [];
	try {
		const longTasks = new PerformanceObserver((list) => {
			for (const entry of list.getEntries()) {
				if (entry.duration >= 100) {
					console.warn(`[performance] Long main-thread task: ${entry.duration.toFixed(0)} ms`, entry);
				}
			}
		});
		longTasks.observe({ type: "longtask", buffered: true });
		observers.push(longTasks);
	} catch {
		// Some development browsers do not expose the optional long-task entry type.
	}

	return () => {
		for (const observer of observers) {
			observer.disconnect();
		}
	};
}
