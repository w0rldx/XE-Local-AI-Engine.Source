// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// jsdom has no WebGL, and what is under test is what the viewer BUILDS from a scene, not three's rendering. Plain
// classes record the meshes and disposals; `webgl` decides whether the renderer can be created at all.
const three = vi.hoisted(() => ({
	webgl: true,
	meshes: [] as string[],
	disposed: [] as string[],
	sizes: [] as [number, number][],
}));
vi.mock("three", () => {
	class Vector {
		set() {
			return this;
		}
	}
	class Node {
		position = new Vector();
		rotation = new Vector();
		scale = new Vector();
		add() {
			return this;
		}
		lookAt() {
			return this;
		}
		dispose() {
			three.disposed.push(this.constructor.name);
		}
	}
	const geometry = (name: string) =>
		class {
			kind = name;
			rotateX() {
				return this;
			}
			dispose() {
				three.disposed.push(name);
			}
		};
	class Mesh extends Node {
		geometry: { kind: string; dispose: () => void };
		material: { dispose: () => void };
		constructor(geometry: { kind: string; dispose: () => void }, material: { dispose: () => void }) {
			super();
			this.geometry = geometry;
			this.material = material;
			three.meshes.push(geometry.kind);
		}
	}
	class WebGLRenderer {
		domElement = document.createElement("canvas");
		constructor() {
			if (!three.webgl) {
				throw new Error("Error creating WebGL context.");
			}
		}
		setPixelRatio = () => undefined;
		setSize = (width: number, height: number) => {
			three.sizes.push([width, height]);
		};
		render = () => undefined;
		dispose() {
			three.disposed.push("renderer");
		}
	}
	const point = () => ({ x: 0, y: 0, z: 0 });
	class Sphere {
		center = point();
		radius = 1;
	}
	class Box3 {
		min = point();
		max = point();
		setFromObject() {
			return this;
		}
		getBoundingSphere(sphere: Sphere) {
			return sphere;
		}
	}
	return {
		Box3,
		BoxGeometry: geometry("box"),
		SphereGeometry: geometry("sphere"),
		CylinderGeometry: geometry("cylinder"),
		ConeGeometry: geometry("cone"),
		PlaneGeometry: geometry("plane"),
		Color: class {},
		DirectionalLight: Node,
		GridHelper: Node,
		HemisphereLight: Node,
		Sphere,
		MathUtils: { degToRad: (degrees: number) => (degrees * Math.PI) / 180 },
		Mesh,
		MeshStandardMaterial: class {
			dispose() {
				three.disposed.push("material");
			}
		},
		PerspectiveCamera: class extends Node {
			aspect = 1;
			updateProjectionMatrix = () => undefined;
		},
		Scene: Node,
		WebGLRenderer,
	};
});
vi.mock("three/examples/jsm/controls/OrbitControls.js", () => ({
	OrbitControls: class {
		target = { set: () => undefined };
		update = () => undefined;
		addEventListener = () => undefined;
		removeEventListener = () => undefined;
		dispose() {
			three.disposed.push("controls");
		}
	},
}));

import BenchmarkSceneViewer from "@/features/benchmarks/components/BenchmarkSceneViewer";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";
import { renderWithProviders } from "@/test/RenderWithProviders";

const answer = `\`\`\`json
${JSON.stringify({
	version: 1,
	objects: [
		{ id: "floor", primitive: "plane", position: [0, 0, 0], scale: [10, 1, 10] },
		{ id: "ball", primitive: "sphere", position: [0, 1, 0] },
		{ id: "post", primitive: "cylinder", position: [2, 0.5, 0] },
	],
})}
\`\`\``;

beforeEach(() => {
	three.webgl = true;
	three.meshes = [];
	three.disposed = [];
	three.sizes = [];
});
afterEach(cleanup);

describe("BenchmarkSceneViewer", () => {
	it("builds one mesh per object and labels the canvas", () => {
		renderWithProviders(<BenchmarkSceneViewer answer={answer} />);

		expect(three.meshes).toEqual(["plane", "sphere", "cylinder"]);
		expect(screen.getByRole("img", { name: "3D scene with 3 objects" }).tagName).toBe("CANVAS");
	});

	it("follows the container's width when the layout changes", () => {
		// renderWithProviders reinstalls the shared ResizeObserver stub on every render, so this test installs the
		// stubs once itself, swaps in (by assignment: the stub is writable, not configurable) an observer that hands
		// back its callback, renders through the plain helper, and puts the stub back.
		const observed: ResizeObserverCallback[] = [];
		installJsdomEnvironmentMocks();
		const shared = window.ResizeObserver;
		window.ResizeObserver = class implements ResizeObserver {
			constructor(callback: ResizeObserverCallback) {
				observed.push(callback);
			}
			observe = () => undefined;
			unobserve = () => undefined;
			disconnect = () => undefined;
		};
		try {
			renderWithMantine(<BenchmarkSceneViewer answer={answer} />);
			Object.defineProperty(screen.getByTestId("benchmark-scene-viewer"), "clientWidth", { value: 240 });

			for (const callback of observed) {
				callback([], new shared(() => undefined));
			}

			expect(three.sizes.at(-1)).toEqual([240, 320]);
		} finally {
			window.ResizeObserver = shared;
		}
	});

	it("disposes everything it created on unmount", () => {
		const view = renderWithProviders(<BenchmarkSceneViewer answer={answer} />);

		view.unmount();

		expect(three.disposed.filter((name) => name === "material")).toHaveLength(3);
		expect(three.disposed).toEqual(expect.arrayContaining(["plane", "sphere", "cylinder", "controls", "renderer"]));
	});

	it("says so instead of drawing when the browser has no WebGL", () => {
		three.webgl = false;
		renderWithProviders(<BenchmarkSceneViewer answer={answer} />);

		expect(screen.getByTestId("benchmark-scene-unavailable").textContent).toBe("3D preview unavailable in this browser.");
		expect(screen.queryByRole("img")).toBeNull();
	});

	it("shows the parse error and builds nothing for an invalid scene", () => {
		renderWithProviders(
			<BenchmarkSceneViewer answer='{"version":1,"objects":[{"id":"a","primitive":"torus","position":[0,0,0]}]}' />,
		);

		expect(screen.getByTestId("benchmark-scene-invalid").textContent).toBe(
			"Not a valid scene: object 1: primitive must be one of box, sphere, cylinder, cone, plane",
		);
		expect(three.meshes).toEqual([]);
	});
});
