"""Unit tests for the pure logic in scripts/model-matrix-driver.py: tiers, hashes, the wire rules and the verdict.

`unittest.TestCase` because two runners execute this file: pytest (python-quality) and
scripts/run-release-contract-tests.sh as a bare `python3 <file>`. The subject's filename is not a valid module name, so
it is loaded through importlib. No node, no model, no GPU.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[2] / "scripts" / "model-matrix-driver.py"
SPEC = importlib.util.spec_from_file_location("model_matrix_driver", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise ImportError(MODULE_PATH)
driver = importlib.util.module_from_spec(SPEC)
sys.modules["model_matrix_driver"] = driver
SPEC.loader.exec_module(driver)

MANIFEST = json.loads((MODULE_PATH.parent / "model-matrix" / "models.json").read_text(encoding="utf-8"))


def results(*checks: tuple[str, str, str]) -> dict:
    """One model whose checks are (name, kind, verdict)."""
    return {
        "models": [
            {
                "id": "m",
                "checks": {name: {"kind": kind, "verdict": verdict, "detail": ""} for name, kind, verdict in checks},
            }
        ]
    }


class ManifestTests(unittest.TestCase):
    def test_tiers_are_cumulative_and_match_the_plan(self) -> None:
        ids = {tier: [m["id"] for m in driver.resolve_models(MANIFEST, tier, [])] for tier in driver.TIERS}
        self.assertEqual(ids["fast"], ["qwen3.5-0.8b", "qwen3.5-4b"])
        self.assertEqual(
            sorted(ids["extended"]), sorted(ids["fast"] + ["qwen3.5-9b", "granite-4.1-3b", "lfm2.5-8b-a1b"])
        )
        self.assertEqual(sorted(ids["rc"]), sorted(ids["extended"] + ["qwen3.6-35b-a3b", "qwen3.8-27b"]))

    def test_every_entry_is_pinned(self) -> None:
        for model in MANIFEST["models"]:
            self.assertRegex(model["sha256"], r"^[0-9a-f]{64}$", model["id"])
            self.assertGreater(model["sizeBytes"], 0, model["id"])
            self.assertTrue(set(model["tiers"]) <= set(driver.TIERS), model["id"])

    def test_every_entry_says_how_thinking_is_switched_off(self) -> None:
        switches = {m["id"]: m["thinkingOff"] for m in MANIFEST["models"]}
        self.assertEqual(switches["lfm2.5-8b-a1b"], "budget")
        self.assertIsNone(switches["granite-4.1-3b"])
        self.assertEqual({v for k, v in switches.items() if k.startswith("qwen")}, {"template"})

    def test_an_unknown_or_missing_thinking_switch_is_refused(self) -> None:
        for broken in ({"thinkingOff": "maybe"}, {}):
            entry = {"id": "x", "tiers": ["fast"], **broken}
            with self.assertRaisesRegex(ValueError, "thinkingOff must be"):
                driver.resolve_models({"models": [entry]}, "fast", [])

    def test_explicit_ids_win_and_keep_their_order(self) -> None:
        picked = driver.resolve_models(MANIFEST, "fast", ["granite-4.1-3b", "qwen3.5-0.8b"])
        self.assertEqual([m["id"] for m in picked], ["granite-4.1-3b", "qwen3.5-0.8b"])

    def test_unknown_tier_or_id_is_refused(self) -> None:
        with self.assertRaises(ValueError):
            driver.resolve_models(MANIFEST, "nightly", [])
        with self.assertRaises(ValueError):
            driver.resolve_models(MANIFEST, "fast", ["not-a-model"])

    def test_checks_resolve_to_all_or_a_validated_subset(self) -> None:
        self.assertEqual(driver.resolve_checks(""), list(driver.HARD_CHECKS + driver.MEASURED_CHECKS))
        self.assertEqual(driver.resolve_checks("eject, tool-call"), ["eject", "tool-call"])
        with self.assertRaises(ValueError):
            driver.resolve_checks("eject,bogus")
        with self.assertRaises(ValueError):
            driver.resolve_checks(",")


class LocalFileTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.addCleanup(self.tmp.cleanup)
        self.entry = {"modelName": "org/Model-GGUF:Q4_K_M", "sha256": hashlib.sha256(b"weights").hexdigest()}

    def install(self, data: bytes) -> None:
        (self.dir / "org-model-q4.gguf").write_bytes(data)
        sidecar = {"ModelName": self.entry["modelName"], "LocalFileName": "org-model-q4.gguf"}
        (self.dir / "org-model-q4.gguf.xe-model.json").write_text(json.dumps(sidecar), encoding="utf-8")

    def test_missing_without_a_sidecar_or_file(self) -> None:
        self.assertEqual(driver.file_state(self.dir, self.entry)[0], "missing")
        self.install(b"weights")
        (self.dir / "org-model-q4.gguf").unlink()
        self.assertEqual(driver.file_state(self.dir, self.entry)[0], "missing")

    def test_matching_hash_is_ok(self) -> None:
        self.install(b"weights")
        self.assertEqual(driver.file_state(self.dir, self.entry), ("ok", self.entry["sha256"]))

    def test_other_bytes_are_a_mismatch(self) -> None:
        self.install(b"tampered")
        state, actual = driver.file_state(self.dir, self.entry)
        self.assertEqual(state, "mismatch")
        self.assertEqual(actual, hashlib.sha256(b"tampered").hexdigest())


class WireRuleTests(unittest.TestCase):
    def test_default_limit_is_budget_plus_cap(self) -> None:
        # Shipped defaults at 65,536: low budget 2048 + cap min(32768, 16384) — the live round's n_predict 18432.
        self.assertEqual(driver.expected_default_limit({}, 65536, reasoning=True), 18432)
        # A non-reasoning model gets no budget: half the window at 4,096 (Granite's n_predict 2048).
        self.assertEqual(driver.expected_default_limit({}, 4096, reasoning=False), 2048)
        # The sum never exceeds the window (the 4B's n_predict 4096 at a 4,096 window).
        self.assertEqual(driver.expected_default_limit({}, 4096, reasoning=True), 4096)

    def test_default_limit_follows_the_settings(self) -> None:
        settings = {"defaultReasoningEffort": "minimal", "reasoningBudgetMinimalTokens": 256}
        self.assertEqual(driver.expected_default_limit(settings, 65536, reasoning=True), 16640)
        self.assertEqual(driver.expected_default_limit({"reasoningBudgetLowTokens": 512}, 65536, reasoning=True), 16896)
        self.assertEqual(driver.expected_default_limit({"chatOutputCapMaxTokens": 1000}, 65536, reasoning=False), 1000)
        self.assertEqual(driver.expected_default_limit({"chatOutputCapMode": "notice"}, 65536, reasoning=True), -1)
        self.assertEqual(driver.expected_default_limit({"chatOutputCapMode": "off"}, 65536, reasoning=True), -1)

    def test_thinking_off_is_a_closed_empty_block(self) -> None:
        self.assertTrue(driver.thinking_off("<|im_start|>assistant\n<think>\n\n</think>\n\n"))
        self.assertFalse(driver.thinking_off("<|im_start|>assistant\n<think>\n"))
        self.assertFalse(driver.thinking_off(None))

    def test_background_jobs_template_switch_needs_cap_and_closed_block(self) -> None:
        off = {"idTask": 1, "nPredict": 1024, "generationPromptTail": "assistant\n<think>\n\n</think>\n\n"}
        on = {"idTask": 2, "nPredict": 1024, "generationPromptTail": "assistant\n<think>\n"}
        self.assertEqual(driver.background_job_problems([off], "template"), [])
        self.assertEqual(driver.background_job_problems([on], "template"), ["task 2 thinking on"])

    def test_background_jobs_budget_switch_needs_only_the_cap(self) -> None:
        # LFM2.5: thinking is off through a zero budget, so the prompt ends in the bare assistant header.
        capped = {"idTask": 3, "nPredict": 1024, "generationPromptTail": "<|im_start|>assistant\n"}
        uncapped = {"idTask": 4, "nPredict": -1, "generationPromptTail": "<|im_start|>assistant\n"}
        self.assertEqual(driver.background_job_problems([capped], "budget"), [])
        self.assertEqual(driver.background_job_problems([uncapped], "budget"), ["task 4 uncapped (n_predict -1)"])

    def test_expert_offload_flags(self) -> None:
        resident = ["llama-server", "-m", "x.gguf", "-c", "65536", "-ngl", "999"]
        self.assertEqual(driver.expert_offload_flags(resident), [])
        self.assertEqual(driver.argv_value(resident, "-c", "--ctx-size"), "65536")
        self.assertEqual(driver.expert_offload_flags([*resident, "--cpu-moe"]), ["--cpu-moe"])
        self.assertEqual(
            driver.expert_offload_flags([*resident, "-ot", "blk.*ffn_.*_exps.=CPU"]), ["-ot blk.*ffn_.*_exps.=CPU"]
        )
        self.assertEqual(driver.expert_offload_flags([*resident, "-ot", "token_embd=CPU"]), [])


def sample(tokens: int | None, notice: bool = False, outcome: str = "assistant-completed") -> dict:
    return {"outcome": outcome, "outputTokens": tokens, "notices": ["OutputLimitReached"] if notice else []}


class LengthLimitVerdictTests(unittest.TestCase):
    LIMIT = 64

    def verdict(self, *samples: dict) -> str:
        return driver.length_limit_verdict(list(samples), self.LIMIT)[0]

    def test_reached_with_the_notice_passes(self) -> None:
        self.assertEqual(self.verdict(sample(64, notice=True)), "pass")

    def test_reached_without_the_notice_fails(self) -> None:
        self.assertEqual(self.verdict(sample(64)), "fail")

    def test_exceeding_the_limit_fails_with_or_without_the_notice(self) -> None:
        self.assertEqual(self.verdict(sample(65, notice=True)), "fail")
        self.assertEqual(self.verdict(sample(65)), "fail")

    def test_a_notice_below_the_limit_fails(self) -> None:
        self.assertEqual(self.verdict(sample(40, notice=True)), "fail")

    def test_a_natural_stop_proves_nothing_and_the_next_sample_decides(self) -> None:
        self.assertEqual(self.verdict(sample(40)), "n/a")
        self.assertEqual(self.verdict(sample(40), sample(64, notice=True)), "pass")
        self.assertEqual(self.verdict(sample(40), sample(64)), "fail")

    def test_never_reaching_the_limit_in_three_samples_is_not_applicable(self) -> None:
        code, reason = driver.length_limit_verdict([sample(40), sample(52), sample(33)], self.LIMIT)
        self.assertEqual(code, "n/a")
        self.assertIn("no sample reached the limit in 3", reason)

    def test_a_failed_turn_or_missing_count_fails(self) -> None:
        self.assertEqual(self.verdict(sample(None)), "fail")
        self.assertEqual(self.verdict(sample(64, notice=True, outcome="assistant-failed")), "fail")


class StubSession:
    """Just enough of driver.Session for check_thinking_off: canned turns and a canned stored answer."""

    def __init__(
        self, switch: str | None, reasoning_chars: int, prompt_tail: str, stored: str | None = "42", notices=()
    ) -> None:
        self.entry = {"thinkingOff": switch}
        self.stored = stored
        self.prompts: list[str] = []
        self.result = {
            "label": "thinking-off",
            "outcome": "assistant-completed",
            "seconds": 0.1,
            "contentChars": len((stored or "").strip()),
            "reasoningChars": reasoning_chars,
            "notices": list(notices),
            "toolsCompleted": [],
            "error": None,
            "wire": [{"nPredict": 16384, "temperature": 0.8, "generationPromptTail": prompt_tail}],
        }

    def turn(self, _label: str, prompt: str, **_kwargs: object) -> dict:
        self.prompts.append(prompt)
        return self.result

    def persisted(self, _turn: dict) -> str | None:
        return self.stored


class WindowToolsVerdictTests(unittest.TestCase):
    FIT = "The tools offered with this message do not fit this model's context window — Turn off tools for this chat."
    ROUND = "This request is too large for the model's context window even after trimming the conversation — start."
    COMPACT = "Conversation exceeds the model's context window even after truncation — Compact the conversation."

    @staticmethod
    def turn(outcome: str, error: str | None = None, notices=(), content: int = 3) -> dict:
        return {"outcome": outcome, "error": error, "notices": list(notices), "contentChars": content}

    def test_a_narrowed_completed_turn_passes(self) -> None:
        turn = self.turn("assistant-completed", notices=["ToolsFiltered"])
        self.assertEqual(driver.window_tools_verdict(turn, [4096]), ("pass", "completed"))

    def test_the_two_product_refusals_pass(self) -> None:
        self.assertEqual(
            driver.window_tools_verdict(self.turn("assistant-failed", self.FIT), []), ("pass", "refused-at-fit")
        )
        self.assertEqual(
            driver.window_tools_verdict(self.turn("assistant-failed", self.ROUND), [4096]),
            ("pass", "refused-before-round-two"),
        )

    def test_everything_else_fails(self) -> None:
        cases = [
            (self.turn("assistant-completed"), [4096]),  # no ToolsFiltered notice
            (self.turn("assistant-completed", notices=["ToolsFiltered"], content=0), [4096]),
            (self.turn("assistant-completed", notices=["ToolsFiltered"]), [8192]),  # the pin did not hold
            (self.turn("assistant-failed", self.ROUND), [4096, 8192]),
            (self.turn("assistant-failed", "Provider returned HTTP 400: context size exceeded"), [4096]),
            (self.turn("assistant-failed", self.COMPACT), []),
            (self.turn("assistant-failed", None), []),
            (self.turn("no-terminal-event"), []),
            (self.turn("driver-error", "the chat stream did not complete within 600s"), []),
        ]
        for turn, windows in cases:
            self.assertEqual(driver.window_tools_verdict(turn, windows)[0], "fail", (turn, windows))


class ExploreOrderTests(unittest.TestCase):
    class Session:
        """Records the order of ejects and explores; the explore answers come from a script."""

        def __init__(self, answers: list[tuple[int, object]]) -> None:
            self.model, self.calls, self.answers, self.client = "m", [], answers, self

        def eject(self) -> bool:
            self.calls.append("eject")
            return True

        def post(self, path: str, _body: object, allowed: tuple[int, ...] = ()) -> tuple[int, object]:
            self.calls.append(path.rsplit("/", 1)[-1])
            return self.answers.pop(0)

    CONFLICT = (500, {"detail": "The profiling launch conflicts with another in-flight admission."})

    def test_every_explore_follows_an_eject_and_a_pending_admission_is_retried(self) -> None:
        session = self.Session([self.CONFLICT, self.CONFLICT, (200, {"profile": {"profileId": "p"}})])
        status, _ = driver.explore_after_eject(session, pause=0)
        self.assertEqual(status, 200)
        self.assertEqual(session.calls, ["eject", "explore"] * 3)

    def test_another_refusal_is_not_retried_and_retries_are_bounded(self) -> None:
        session = self.Session([(500, {"detail": "boom"})])
        self.assertEqual(driver.explore_after_eject(session, pause=0)[0], 500)
        self.assertEqual(session.calls, ["eject", "explore"])
        session = self.Session([self.CONFLICT] * 3)
        self.assertEqual(driver.explore_after_eject(session, attempts=3, pause=0)[0], 500)
        self.assertEqual(len(session.calls), 6)


class ThinkingOffCheckTests(unittest.TestCase):
    CLOSED, OPEN, BARE = "<think>\n\n</think>\n\n", "<think>\n", "<|im_start|>assistant\n"

    def verdict(self, session: StubSession) -> str:
        return driver.check_thinking_off(session)["thinking-off"]["verdict"]

    def test_template_switch_needs_no_reasoning_and_a_closed_block_on_the_wire(self) -> None:
        self.assertEqual(self.verdict(StubSession("template", 0, self.CLOSED)), "pass")
        self.assertEqual(self.verdict(StubSession("template", 0, self.OPEN)), "fail")
        self.assertEqual(self.verdict(StubSession("template", 40, self.CLOSED)), "fail")

    def test_budget_switch_allows_reasoning_but_needs_a_clean_answer_to_both_prompts(self) -> None:
        session = StubSession("budget", 218, self.BARE)
        self.assertEqual(self.verdict(session), "pass")
        self.assertEqual(session.prompts, ["Say OK.", "What is 17 + 25? Reply with just the number."])

    def test_budget_switch_fails_on_the_broken_shapes(self) -> None:
        # The shape the live run found: the only answer filed as reasoning, an empty answer and an EmptyAnswer notice.
        self.assertEqual(self.verdict(StubSession("budget", 5, self.BARE, "", ["EmptyAnswer"])), "fail")
        self.assertEqual(self.verdict(StubSession("budget", 0, self.BARE, "OK", ["EmptyAnswer"])), "fail")
        self.assertEqual(self.verdict(StubSession("budget", 0, self.BARE, "</think>\nOK")), "fail")
        self.assertEqual(self.verdict(StubSession("budget", 0, self.BARE, None)), "fail")

    def test_a_model_that_does_not_think_is_not_applicable(self) -> None:
        self.assertEqual(self.verdict(StubSession(None, 0, self.BARE)), "n/a")


class OwnedServerTests(unittest.TestCase):
    HOST = "/work/checkout/XE-Local-AI-Engine.Client/"
    OTHER = "/work/checkout/.tmp/worktrees/other/XE-Local-AI-Engine.Client/"

    @staticmethod
    def proc(ppid: int, *argv: str, exe: str = "") -> dict:
        return {"ppid": ppid, "argv": list(argv), "exe": exe}

    def table(self) -> dict[int, dict]:
        server = "/opt/llama/bin/llama-server"
        return {
            1: self.proc(0, "/sbin/init"),
            10: self.proc(1, "dcp"),
            # This checkout's host, run as `dotnet <dll>`, and the server it spawned.
            20: self.proc(10, "dotnet", self.HOST + "bin/Debug/net10.0/XE-Local-AI-Engine.Client.dll"),
            21: self.proc(20, server, "-m", "/models/qwen.gguf", "--port", "40001"),
            # Another checkout nested under this one serves the same file: its path only shares a prefix.
            30: self.proc(10, "x", exe=self.OTHER + "bin/XE-Local-AI-Engine.Client"),
            31: self.proc(30, server, "-m", "/models/qwen.gguf", "--port", "40002"),
            # An orphan of a dead host, and this host's server of another model.
            40: self.proc(1, server, "-m", "/models/qwen.gguf"),
            22: self.proc(20, server, "-m", "/models/other.gguf"),
        }

    def test_only_the_server_descending_from_this_checkouts_host_is_owned(self) -> None:
        self.assertEqual(driver.owned_servers(self.table(), "qwen.gguf", self.HOST), [21])

    def test_an_empty_model_file_selects_every_server_of_this_host(self) -> None:
        self.assertEqual(sorted(driver.owned_servers(self.table(), "", self.HOST)), [21, 22])

    def test_the_host_is_recognised_by_its_executable_and_through_intermediate_ancestors(self) -> None:
        table = self.table()
        table[20] = self.proc(10, "XE-Local-AI-Engine.Client", exe=self.HOST + "bin/XE-Local-AI-Engine.Client")
        table[21]["ppid"] = 25
        table[25] = self.proc(20, "/usr/bin/setsid")
        self.assertEqual(driver.owned_servers(table, "qwen.gguf", self.HOST), [21])

    def test_a_parent_cycle_ends_the_walk(self) -> None:
        table = {5: self.proc(6, "llama-server", "-m", "qwen.gguf"), 6: self.proc(5, "sh")}
        self.assertEqual(driver.owned_servers(table, "qwen.gguf", self.HOST), [])


class BackendTests(unittest.TestCase):
    @staticmethod
    def audit(backend: str, fallback: bool = False, reason: str | None = None) -> dict:
        return {"inferenceBackend": backend, "cpuFallback": fallback, "cpuFallbackReason": reason}

    def test_the_matching_backend_passes(self) -> None:
        self.assertIsNone(driver.backend_problem("cuda", self.audit("cuda")))
        self.assertIsNone(driver.backend_problem("cpu", self.audit("cpu")))

    def test_cpu_requested_on_a_gpu_backend_is_refused_with_the_way_out(self) -> None:
        problem = driver.backend_problem("cpu", self.audit("cuda")) or ""
        self.assertIn("--variant cpu was requested, but the node's device audit reports backend 'cuda'", problem)
        self.assertIn("XE_LLAMACPP_SERVER_PATH", problem)

    def test_cuda_requested_needs_cuda_without_a_fallback(self) -> None:
        problem = driver.backend_problem("cuda", self.audit("cpu", True, "no CUDA device")) or ""
        self.assertIn("backend 'cpu' with a CPU fallback (no CUDA device)", problem)
        self.assertIsNotNone(driver.backend_problem("cuda", self.audit("cuda", True)))
        self.assertIsNotNone(driver.backend_problem("cuda", self.audit("vulkan")))
        self.assertIsNotNone(driver.backend_problem("cuda", self.audit("unknown")))
        self.assertIsNotNone(driver.backend_problem("cpu", self.audit("unknown")))


class VerdictTests(unittest.TestCase):
    def test_an_empty_run_is_not_a_pass(self) -> None:
        self.assertEqual(driver.judge({"models": []})[0], 1)
        self.assertEqual(driver.judge({})[0], 1)

    def test_only_not_applicable_hard_checks_is_not_a_pass(self) -> None:
        self.assertEqual(
            driver.judge(results(("moe-placement", "hard", "n/a"), ("vram", "measured", "recorded")))[0], 1
        )

    def test_a_failed_hard_check_fails_the_run(self) -> None:
        code, lines = driver.judge(results(("load-chat", "hard", "pass"), ("thinking-off", "hard", "fail")))
        self.assertEqual(code, 1)
        self.assertIn("FAIL m thinking-off: ", lines)

    def test_a_low_measured_value_does_not(self) -> None:
        run = results(("load-chat", "hard", "pass"), ("multi-tool", "measured", "recorded"))
        run["models"][0]["checks"]["multi-tool"]["value"] = "0/3"
        self.assertEqual(driver.judge(run), (0, ["1/1 hard checks passed"]))

    def test_summary_shows_the_grid(self) -> None:
        run = results(("load-chat", "hard", "pass"), ("eject", "hard", "fail"), ("moe-placement", "hard", "n/a"))
        run.update(tier="fast", variant="cuda", backend={"inferenceBackend": "cuda"})
        text = driver.summary_markdown(run)
        self.assertIn("variant `cuda` (device audit: backend `cuda`)", text)
        self.assertIn("| load-chat (hard) | pass |", text)
        self.assertIn("| eject (hard) | **FAIL** |", text)
        self.assertIn("| moe-placement (hard) | n/a |", text)
        self.assertIn("verdict **FAIL**", text)


if __name__ == "__main__":
    unittest.main()
