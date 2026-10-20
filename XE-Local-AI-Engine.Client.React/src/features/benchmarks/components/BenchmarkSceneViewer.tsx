import { Box, Text } from "@mantine/core";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
	Box3,
	BoxGeometry,
	type BufferGeometry,
	Color,
	ConeGeometry,
	CylinderGeometry,
	DirectionalLight,
	GridHelper,
	HemisphereLight,
	MathUtils,
	Mesh,
	MeshStandardMaterial,
	PerspectiveCamera,
	PlaneGeometry,
	Scene,
	Sphere,
	SphereGeometry,
	WebGLRenderer,
} from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";

import type { BenchmarkScenePrimitive } from "@/features/benchmarks/models/BenchmarkSceneSpec";
import { parseSceneSpec, sceneCameraDistance, sceneCameraFov } from "@/features/benchmarks/models/BenchmarkSceneSpec";

const height = 320;

// Unit primitives, as the verifier's bounds assume them: every one fits a 1×1×1 box, and the plane lies in XZ.
const geometries: Record<BenchmarkScenePrimitive, () => BufferGeometry> = {
	box: () => new BoxGeometry(1, 1, 1),
	sphere: () => new SphereGeometry(0.5, 32, 16),
	cylinder: () => new CylinderGeometry(0.5, 0.5, 1, 32),
	cone: () => new ConeGeometry(0.5, 1, 32),
	plane: () => new PlaneGeometry(1, 1).rotateX(-Math.PI / 2),
};

/**
 * The graded answer's `sceneSpec` scene, drawn with fixed lighting and an auto-framed camera. Display only: the score
 * is the node's verifier verdict, and this draws the parsed data and nothing else — no textures, URLs or code.
 */
export default function BenchmarkSceneViewer({ answer }: { answer: string }) {
	const { t } = useTranslation();
	const parsed = useMemo(() => parseSceneSpec(answer), [answer]);
	const scene = "scene" in parsed ? parsed.scene : null;
	const container = useRef<HTMLDivElement>(null);
	const [unavailable, setUnavailable] = useState(false);
	const label = t("pages.benchmarks.scene.label", "3D scene with {{objects}} objects", {
		objects: scene?.objects.length ?? 0,
	});

	useEffect(() => {
		const host = container.current;
		if (scene === null || host === null) {
			return;
		}
		let renderer: WebGLRenderer;
		try {
			renderer = new WebGLRenderer({ antialias: true });
		} catch {
			// No WebGL (a locked-down browser, a headless test): the text below replaces the canvas.
			setUnavailable(true);
			return;
		}
		setUnavailable(false);
		const width = host.clientWidth || 480;
		renderer.setPixelRatio(window.devicePixelRatio);
		renderer.setSize(width, height);
		renderer.domElement.setAttribute("role", "img");
		renderer.domElement.setAttribute("aria-label", label);
		host.appendChild(renderer.domElement);

		const world = new Scene();
		world.background = new Color("#f4f4f5");
		const meshes = scene.objects.map((object) => {
			const mesh = new Mesh(geometries[object.primitive](), new MeshStandardMaterial({ color: object.color }));
			mesh.position.set(...object.position);
			const [x, y, z] = object.rotation;
			mesh.rotation.set(MathUtils.degToRad(x), MathUtils.degToRad(y), MathUtils.degToRad(z));
			mesh.scale.set(...object.scale);
			world.add(mesh);
			return mesh;
		});
		// Fit what is rendered, rotation included: the verifier's axis-aligned bounds ignore rotation, and a rotated,
		// elongated object reaches outside them. Taken before the grid joins the scene so it does not widen the fit.
		const box = new Box3().setFromObject(world);
		const sphere = box.getBoundingSphere(new Sphere());
		const { center } = sphere;
		const radius = Math.max(sphere.radius, 0.5);
		const grid = new GridHelper(Math.ceil(radius * 4), 20);
		grid.position.set(center.x, box.min.y, center.z);
		world.add(grid);
		world.add(new HemisphereLight("#ffffff", "#444444", 1.5));
		const sun = new DirectionalLight("#ffffff", 1.5);
		sun.position.set(center.x + radius * 2, center.y + radius * 3, center.z + radius);
		world.add(sun);

		const camera = new PerspectiveCamera(sceneCameraFov, width / height, radius / 100, radius * 100);
		const controls = new OrbitControls(camera, renderer.domElement);
		controls.target.set(center.x, center.y, center.z);
		// From above and to the front right, far enough that the bounding sphere fills the narrower field of view.
		// Called again on resize because the fit depends on the aspect; that puts an orbited camera back in place.
		const frame = (aspect: number) => {
			camera.aspect = aspect;
			camera.updateProjectionMatrix();
			const distance = sceneCameraDistance(radius, aspect);
			camera.position.set(center.x + distance * 0.6, center.y + distance * 0.5, center.z + distance * 0.62);
			controls.update();
		};
		frame(width / height);
		const render = () => renderer.render(world, camera);
		controls.addEventListener("change", render);
		render();
		// The pane's width changes with the layout; the canvas follows it or it clips.
		const observer = new ResizeObserver(() => {
			const next = host.clientWidth || width;
			renderer.setSize(next, height);
			frame(next / height);
			render();
		});
		observer.observe(host);

		return () => {
			observer.disconnect();
			controls.removeEventListener("change", render);
			controls.dispose();
			for (const mesh of meshes) {
				mesh.geometry.dispose();
				(mesh.material as MeshStandardMaterial).dispose();
			}
			grid.dispose();
			renderer.dispose();
			renderer.domElement.remove();
		};
	}, [scene, label]);

	if ("error" in parsed) {
		return (
			<Text size="sm" c="red" data-testid="benchmark-scene-invalid">
				{t("pages.benchmarks.scene.invalid", "Not a valid scene: {{reason}}", { reason: parsed.error })}
			</Text>
		);
	}
	return unavailable ? (
		<Text size="sm" c="dimmed" data-testid="benchmark-scene-unavailable">
			{t("pages.benchmarks.scene.unavailable", "3D preview unavailable in this browser.")}
		</Text>
	) : (
		<Box ref={container} h={height} data-testid="benchmark-scene-viewer" />
	);
}
