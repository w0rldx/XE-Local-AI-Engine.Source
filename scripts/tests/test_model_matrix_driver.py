"""Unit tests for the pure logic in scripts/model-matrix-driver.py: tiers, hashes, the wire rules and the verdict.

`unittest.TestCase` because two runners execute this file: pytest (python-quality) and
scripts/run-release-contract-tests.sh as a bare `python3 <file>`. The subject's filename is not a valid module name, so
it is loaded through importlib. No node, no model, no GPU.
"""

from __future__ import annotations

import email.message
import hashlib
import importlib.util
import io
import json
import re
import sys
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest import mock

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
        self.assertEqual(sorted(ids["rc"]), sorted(ids["extended"] + ["qwen3.6-35b-a3b", "qwen3.8-27b", "flint"]))

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
        # Shipped defaults at 65,536: low budget 2048 + cap min(32768, 16384) gives n_predict 18432.
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


class MemoryJobRestoreTests(unittest.TestCase):
    """The probe sets the memory extraction model and must restore what was there, clearing it with null when unset."""

    @staticmethod
    def restore_puts(previous: str | None) -> list[dict]:
        """The node-settings PUT bodies check_memory_job sends when the node held `previous` (the first turn aborts)."""
        puts: list[dict] = []

        class Client:
            def get(self, _path: str) -> dict:
                return {"memoryExtractionModelName": previous}

            def put(self, _path: str, body: dict) -> None:
                puts.append(body)

            def post(self, _path: str, _body: dict) -> tuple[int, dict]:
                return 201, {"id": "agent-1"}

            def delete(self, _path: str) -> None:
                pass

        class Session:
            entry = {"thinkingOff": "template"}
            model = "probe-model"
            client = Client()

            def turn(self, *_args: object, **_kwargs: object) -> dict:
                raise RuntimeError("stop after the setup")

        try:
            driver.check_memory_job(Session())
        except RuntimeError:
            return puts
        raise AssertionError("check_memory_job did not reach the first turn")

    def test_an_unset_memory_model_is_restored_with_null(self) -> None:
        for previous in (None, ""):
            with self.subTest(previous=previous):
                self.assertEqual(
                    self.restore_puts(previous),
                    [{"memoryExtractionModelName": "probe-model"}, {"memoryExtractionModelName": None}],
                )

    def test_a_set_memory_model_is_restored_as_it_was(self) -> None:
        self.assertEqual(self.restore_puts("qwen3:8b")[-1], {"memoryExtractionModelName": "qwen3:8b"})


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


# A 2-slot and a 1-slot 65,536 chat launch, as LlamaServerLaunchArgumentComposer.AppendChatSlotArgs emits them.
TWO_SLOT_ARGV = ["llama-server", "-m", "m.gguf", "-c", "73728", "-np", "2", "--kv-unified-per-slot", "65536"]
ONE_SLOT_ARGV = ["llama-server", "-m", "m.gguf", "-c", "65536", "-np", "1"]


class ExpectedSlotWindowTests(unittest.TestCase):
    def test_the_per_slot_cap_wins_over_the_shared_context(self) -> None:
        self.assertEqual(driver.expected_slot_window(TWO_SLOT_ARGV), "65536")

    def test_a_single_slot_launch_reads_its_context(self) -> None:
        self.assertEqual(driver.expected_slot_window(ONE_SLOT_ARGV), "65536")

    def test_no_context_flag_is_none(self) -> None:
        self.assertIsNone(driver.expected_slot_window(["llama-server", "-m", "m.gguf"]))


class MoePlacementCheckTests(unittest.TestCase):
    class Session:
        """Just enough of driver.Session for check_moe_placement: a MoE on CUDA and a canned running surface."""

        def __init__(self, window: int) -> None:
            self.entry, self.variant, self.model_file = {"moe": True, "sizeBytes": 1 << 30}, "cuda", "m.gguf"
            self.window = window

        def eject(self) -> bool:
            return True

        def turn(self, label: str, _prompt: str, **_kwargs: object) -> dict:
            return {
                "label": label,
                "outcome": "assistant-completed",
                "seconds": 0.1,
                "contentChars": 2,
                "reasoningChars": 0,
                "notices": [],
                "toolsCompleted": [],
                "error": None,
                "wire": [],
            }

        def running(self) -> list[dict]:
            return [{"effectiveContextTokens": self.window, "expertsOffloaded": False}]

    def verdict(self, argv: list[str], window: int) -> str:
        with (
            mock.patch.object(driver, "gpu_memory_mib", return_value=(0, 32768)),
            mock.patch.object(driver, "llama_processes", return_value=[{"argv": argv}]),
        ):
            return driver.check_moe_placement(self.Session(window))["moe-placement"]["verdict"]

    def test_a_two_slot_launch_is_judged_against_its_per_slot_window(self) -> None:
        self.assertEqual(self.verdict(TWO_SLOT_ARGV, 65536), "pass")

    def test_a_surface_that_disagrees_with_the_launch_fails(self) -> None:
        self.assertEqual(self.verdict(TWO_SLOT_ARGV, 73728), "fail")
        self.assertEqual(self.verdict(ONE_SLOT_ARGV, 32768), "fail")


class HttpLogTests(unittest.TestCase):
    """http.jsonl records every call in the current evidence directory, with credentials scrubbed. No network: the
    transport, NodeClient.request, is patched."""

    def test_a_recorded_call_keeps_no_credential(self) -> None:
        answer = {"accessToken": "resp-secret-1", "user": {"refreshToken": "resp-secret-2"}, "id": "agent-1"}
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, answer)):
                client.post("/agents", {"password": "req-secret-3", "title": "t"})
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for secret in ("resp-secret-1", "resp-secret-2", "req-secret-3"):
            self.assertNotIn(secret, text)
        record = json.loads(text)
        self.assertEqual(record["response"]["id"], "agent-1")
        self.assertEqual(json.loads(record["request"]), {"password": "(redacted)", "title": "t"})

    def test_a_cloud_settings_save_records_no_secret_header_value(self) -> None:
        """SaveCloudSettingsRequest as the node binds it: a secret header's value is write-only, a plain one is not."""
        body = {
            "providerName": "AzureFoundry",
            "authMode": "EntraId",
            "apiKey": "cloud-secret-1",
            "entraClientSecret": "cloud-secret-2",
            "headers": [
                {"name": "X-Gateway-Key", "value": "cloud-secret-3", "isSecret": True},
                {"name": "X-Team", "value": "team-a", "isSecret": False},
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, {})):
                client.put("/cloud-settings", body)
                # The same body on a route outside the credential families meets the field scrub (defence in depth).
                client.put("/agents/a1", body)
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for secret in ("cloud-secret-1", "cloud-secret-2", "cloud-secret-3"):
            self.assertNotIn(secret, text)
        cloud, other = (json.loads(line) for line in text.splitlines())
        self.assertTrue(cloud["request"].startswith("[body omitted: credential-bearing route, "))
        headers = json.loads(other["request"])["headers"]
        self.assertEqual(
            headers,
            [
                {"name": "X-Gateway-Key", "value": "(redacted)", "isSecret": True},
                {"name": "X-Team", "value": "team-a", "isSecret": False},
            ],
        )

    def test_names_in_any_case_are_scrubbed_and_recorded_as_sent(self) -> None:
        """The node binds JSON names case-insensitively, so PascalCase and mixed-case names carry the same secrets. The
        route is outside the credential families, so the field scrub alone guards it."""
        body = {
            "Headers": [
                {"Name": "X-Gateway-Key", "Value": "case-secret-1", "IsSecret": True},
                {"name": "X-Other-Key", "VALUE": "case-secret-2", "isSECRET": True},
                {"Name": "X-Team", "Value": "team-a", "IsSecret": False},
            ],
            "Parameters": '{"note": "case-secret-3"}',
            "Variables": {"PORT": "case-secret-4"},
            "Created": {"ApiKey": {"id": "k1"}, "Key": "case-secret-5"},
        }
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, {})):
                client.put("/agents/a1", body)
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for secret in ("case-secret-1", "case-secret-2", "case-secret-3", "case-secret-4", "case-secret-5"):
            self.assertNotIn(secret, text)
        request = json.loads(json.loads(text)["request"])
        self.assertEqual(
            request["Headers"],
            [
                {"Name": "X-Gateway-Key", "Value": "(redacted)", "IsSecret": True},
                {"name": "X-Other-Key", "VALUE": "(redacted)", "isSECRET": True},
                {"Name": "X-Team", "Value": "team-a", "IsSecret": False},
            ],
        )
        self.assertEqual(request["Variables"], {"PORT": "(redacted)"})

    def test_every_field_the_node_masks_on_read_is_scrubbed(self) -> None:
        """Custom-tool headers and environment variables, scheduled-job parameters and external-app variables."""
        body = {
            "httpFetch": {"headers": [{"name": "Authorization", "value": "masked-secret-1", "isSecret": True}]},
            "command": {"env": [{"name": "TOKEN", "value": "masked-secret-2", "isSecret": True}]},
            "parameters": '{"note": "masked-secret-3"}',
            "variables": {"DB_PASSWORD": "masked-secret-4", "PORT": "8080"},
            "tool": {"parameters": [{"name": "query", "required": True}]},
            "hasStoredApiKey": True,
        }
        scrubbed = driver.gsd.scrub(body)
        for secret in ("masked-secret-1", "masked-secret-2", "masked-secret-3", "masked-secret-4"):
            self.assertNotIn(secret, json.dumps(scrubbed))
        self.assertEqual(scrubbed["variables"], {"DB_PASSWORD": "(redacted)", "PORT": "(redacted)"})
        self.assertEqual(scrubbed["tool"], body["tool"])  # a custom tool's parameter list is no credential
        self.assertIs(scrubbed["hasStoredApiKey"], True)

    def test_vault_recovery_codes_and_other_one_time_credentials_are_scrubbed(self) -> None:
        """auth/vault/confirm answers the recovery code; auth/vault/unlock-recovery takes one and answers the next."""
        calls = [
            ("/auth/vault/confirm", {"password": "vault-secret-1"}, {"recoveryCode": "vault-secret-2"}),
            (
                "/auth/vault/unlock-recovery",
                {"RecoveryCode": "vault-secret-3", "newPassword": "vault-secret-4"},
                {"recoveryCode": "vault-secret-5"},
            ),
            ("/cloud-settings/entra/device-code/start", None, {"userCode": "otc-secret-6", "verificationUri": "u"}),
            ("/model-fit/gguf/imports/preview", None, {"previewToken": "otc-secret-7", "errorCode": "kept-code"}),
            ("/probe", {"connectionString": "otc-secret-8", "kvBytesPerToken": 4096}, {}),
        ]
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            for path, body, answer in calls:
                with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, answer)):
                    client.post(path, body)
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for secret in [f"vault-secret-{n}" for n in range(1, 6)] + [f"otc-secret-{n}" for n in range(6, 9)]:
            self.assertNotIn(secret, text)
        records = [json.loads(line) for line in text.splitlines()]
        self.assertEqual(records[0]["response"], "[body omitted: credential-bearing route, 34 bytes]")
        self.assertEqual(records[3]["response"]["errorCode"], "kept-code")  # an error code is an id, not a credential
        self.assertEqual(json.loads(records[4]["request"])["kvBytesPerToken"], 4096)

    def test_a_credential_route_records_no_body(self) -> None:
        """An MCP registration carries credentials under arbitrary header and environment names and in its URL; every
        credential route keeps method, path and status, and a route outside them stays readable."""
        registration = {
            "name": "docs",
            "transportKind": "Http",
            "url": "https://user:mcp-secret-1@mcp.example/sse?token=mcp-secret-2",
            "headers": {"Authorization": "Bearer mcp-secret-3"},
            "env": {"MY_SERVICE": "mcp-secret-4"},
        }
        answer = {"mcpServerId": "m1", "headers": {"Authorization": "Bearer mcp-secret-5"}}
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(201, answer)):
                client.post("/mcp/servers", registration)
                client.put("/MCP/servers/m1?x=1", registration)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, {"id": "agent-1"})):
                client.post("/agents", {"title": "kept"})
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for secret in (f"mcp-secret-{n}" for n in range(1, 6)):
            self.assertNotIn(secret, text)
        create, update, other = (json.loads(line) for line in text.splitlines())
        self.assertEqual(
            (create["method"], create["path"], create["status"]), ("POST", "/api/local/v1/mcp/servers", 201)
        )
        self.assertEqual(update["path"], "/api/local/v1/MCP/servers/m1?x=(redacted)")
        for record in (create, update):
            self.assertRegex(record["request"], r"^\[body omitted: credential-bearing route, \d+ bytes\]$")
            self.assertRegex(record["response"], r"^\[body omitted: credential-bearing route, \d+ bytes\]$")
        self.assertEqual(json.loads(other["request"]), {"title": "kept"})
        self.assertEqual(other["response"], {"id": "agent-1"})

    def test_every_credential_family_and_no_other_route_is_omitted(self) -> None:
        for path in (
            "auth/login",
            "/auth/vault/unlock-recovery",
            "mcp/server-key",
            "cloud-settings/entra/device-code/start",
            "cloud/codex/login",
            "external-providers/connections/c1",
            "external-apps/instances/i1/variables",
            "custom-tools/t1",
            "scheduler/jobs/j1",
            "integrations/keys",
            "proxy/key",
            "model-fit/hf-token",
            "http://127.0.0.1:1/api/local/v1/chat/hub/negotiate?negotiateVersion=1",
        ):
            self.assertTrue(driver.gsd.is_credential_route(path), path)
        for path in ("agents", "authors", "mcp-docs", "external-apps/catalog", "scheduler/runs", "chat/hub?id=x", "/"):
            self.assertFalse(driver.gsd.is_credential_route(path), path)

    def test_a_credential_route_is_classified_as_the_node_routes_it(self) -> None:
        """Dot segments, empty segments, case and percent-encoding do not hide a credential route; an ambiguous
        spelling (encoded slash, backslash or percent sign, ``..`` above the root) counts as one."""
        for path in (
            "./mcp/servers",
            "MCP/Servers",
            "mcp//servers",
            "%6dcp/servers",
            "agents/../mcp/servers",
            "/api/local/v1/./Auth/login",
            "/api/local/v1/x/../auth/login",
            "http://127.0.0.1:1/api/local/v1//chat/hub/./negotiate",
            "mcp%2Fservers",
            "mcp%5cservers",
            "mcp%252fservers",
            "../v1/mcp/servers",
            "agents\\..\\mcp",
        ):
            self.assertTrue(driver.gsd.is_credential_route(path), path)
        for path in ("./agents", "Agents/A1", "agents//a1", "%61gents", "mcp/../agents", "models/a%20b.gguf"):
            self.assertFalse(driver.gsd.is_credential_route(path), path)

    def test_a_path_that_is_not_plain_or_on_the_node_origin_is_a_credential_route(self) -> None:
        """A scheme-relative, other-scheme, other-authority or control-character spelling names no route the driver can
        classify, so its bodies are omitted; a full URL to the node's own origin is classified like its path."""
        origin = "http://127.0.0.1:1"
        for path in (
            "//mcp/servers",
            "//agents",
            "///agents",
            "\\\\mcp\\servers",
            "/\\agents",
            "http:mcp/servers",
            "http:agents",
            "mcp:servers",
            "a1:agents",
            "/\t/mcp/servers",
            "age​nts",
            "http://mcp/servers",
            "http://127.0.0.1:2/api/local/v1/agents",
            "https://127.0.0.1:1/api/local/v1/agents",
            "http://user@127.0.0.1:1/api/local/v1/agents",
            "ftp://127.0.0.1:1/api/local/v1/agents",
        ):
            self.assertTrue(driver.gsd.is_credential_route(path, origin), path)
        self.assertTrue(driver.gsd.is_credential_route("http://127.0.0.1:1/api/local/v1/agents"), "no origin given")
        for path in ("http://127.0.0.1:1/api/local/v1/agents", "HTTP://127.0.0.1:1/api/local/v1/agents", "/agents"):
            self.assertFalse(driver.gsd.is_credential_route(path, origin), path)
        self.assertEqual(
            driver.gsd.redact('{"title": "kept"}', "//agents"), "[body omitted: credential-bearing route, 17 bytes]"
        )

    def test_a_json_shaped_body_that_does_not_parse_is_omitted(self) -> None:
        """A transfer cut after a complete credential, as a whole body and as one SignalR frame; other text stays."""
        cut = '{"items": [{"apiKey": "cut-secret-1"}, {"na'
        self.assertEqual(driver.gsd.redact(cut, "agents"), "[body omitted: unparseable JSON, 43 bytes]")
        self.assertEqual(driver.gsd.redact("  [1, 2", "agents"), "[body omitted: unparseable JSON, 7 bytes]")
        frames = driver.gsd.redact('{"type":1,"arguments":[{"password":"cut-secret-2"' + driver.gsd.RECORD_SEPARATOR)
        self.assertNotIn("cut-secret-2", frames)
        self.assertEqual(driver.gsd.redact("plain text body", "agents"), "plain text body")

    def test_a_credential_value_anywhere_records_nothing_of_it(self) -> None:
        """A key or PEM block in the middle of a text artifact, a JSON string, a JSON name, an error or a logged path
        never reaches evidence; a text without one stays readable."""
        pem = "-----BEGIN PRIVATE KEY-----\nMIIEval-secret-1\n-----END PRIVATE KEY-----\n"
        texts = (
            "run 4 finished\nkey: xeint_val-secret-2 (rotate)\n",
            "notes\n" + pem + "trailer",
            "proxy XEPRX_val-secret-3",
            "  ﻿xemcp_val-secret-4",
            json.dumps({"note": "the key is xeint_val-secret-5", "n": 1}),
            json.dumps({"xemcp_val-secret-6": 1}),
            '{"a": "\\u0078eint_val-secret-7"}',
            json.dumps({"arguments": json.dumps({"q": "see xeprx_val-secret-8"})}),
        )
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            for text in texts:
                with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, text.encode("utf-8"))):
                    client.request("POST", "/benchmarks/runs/r1/artifacts/a", text, expect_binary=True)
            failure = driver.gsd.DriverError("GET /x -> HTTP 500: echoed xeint_val-secret-9")
            with (
                mock.patch.object(driver.gsd.NodeClient, "request", side_effect=failure),
                self.assertRaises(driver.gsd.DriverError) as raised,
            ):
                client.request("GET", "/x")
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, None)):
                client.request("GET", "/agents/xeint_val-secret-10")
            log = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        self.assertNotRegex(log, r"val-secret-")
        self.assertNotIn("val-secret-", str(raised.exception))
        records = [json.loads(line) for line in log.splitlines()]
        self.assertEqual(len(records), len(texts) + 2)
        for record in records[:4]:
            self.assertRegex(record["request"], r"^\[body omitted: credential value, \d+ bytes\]$")
            self.assertRegex(record["response"], r"^\[body omitted: credential value, \d+ bytes\]$")
        self.assertEqual(json.loads(records[4]["request"]), {"note": "(redacted)", "n": 1})
        self.assertEqual(json.loads(records[7]["response"]), {"arguments": "(redacted)"})
        self.assertEqual(records[-1]["path"], "(redacted)")
        self.assertEqual(driver.gsd.redact("run 4 finished\n", "benchmarks/runs/r1/artifacts/a"), "run 4 finished\n")

    def test_a_credential_value_used_as_a_parsed_name_is_never_recorded(self) -> None:
        """A parsed answer and an appended event keep a data-defined map readable, except a name holding a credential
        value, which is recorded as a marker pair."""
        answers = (
            {"usageRates": {"xemcp_name-secret-1": 1, "qwen3-8b": 0.5}},
            {"items": [{"variables": {"XEINT_name-secret-2": "v", "HOME": "/h"}}]},
            {"usageRates": {"qwen3-8b": 1, "olmoe-1b": 2}},
        )
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            for answer in answers:
                with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, answer)):
                    client.request("GET", "/node-settings")
            events = Path(directory) / "events.jsonl"
            driver.gsd.append_jsonl(events, {"t": 1, "data": {"xeprx_name-secret-3": {"n": 1}, "run": "r1"}})
            log = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
            event = events.read_text(encoding="utf-8")
        self.assertNotIn("name-secret-", log + event)
        responses = [json.loads(line)["response"] for line in log.splitlines()]
        self.assertEqual(responses[0], {"usageRates": {"(redacted)": "(redacted)", "qwen3-8b": 0.5}})
        self.assertEqual(responses[1], {"items": [{"variables": {"(redacted)": "(redacted)", "HOME": "(redacted)"}}]})
        self.assertEqual(responses[2], answers[2])
        self.assertEqual(json.loads(event)["data"], {"(redacted)": "(redacted)", "run": "r1"})

    def test_a_body_that_is_not_text_is_omitted(self) -> None:
        """An image or archive (bytes that are not UTF-8, or control characters) leaves a marker, not its bytes;
        JSON behind a NUL and SignalR frames still parse."""
        png = b"\x89PNG\r\n\x1a\nbin-secret-1"
        for raw in (png, b"PK\x03\x04bin-secret-2", b"text\x00bin-secret-3"):
            with self.subTest(raw=raw):
                shown = driver.gsd.redact(raw.decode("utf-8", "replace"), "images/i1")
                self.assertRegex(shown, r"^\[body omitted: not text, \d+ bytes\]$")
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, png)):
                client.request("GET", "/images/i1", expect_binary=True)
            log = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        self.assertNotIn("bin-secret-", log)
        self.assertRegex(json.loads(log)["response"], r"^\[body omitted: not text, \d+ bytes\]$")
        self.assertEqual(driver.gsd.redact("tab\there\r\nline"), "tab\there\r\nline")
        self.assertEqual(driver.gsd.redact('\x00{"password": "p"}'), '{"password": "(redacted)"}')

    def test_a_valid_frame_with_cut_tool_arguments_records_no_credential(self) -> None:
        """A JSON-shaped string inside a frame that does not parse is omitted as a whole, never partly masked."""
        cut_arguments = json.dumps({"password": "cut-secret-3", "query": "x"})[:-12]
        item = {"type": 2, "invocationId": "1", "item": {"toolCall": {"name": "search", "arguments": cut_arguments}}}
        sent = driver.gsd.redact(json.dumps(item) + driver.gsd.RECORD_SEPARATOR, "chat/hub?id=x")
        self.assertNotIn("cut-secret-3", sent)
        frame = json.loads(sent.split(driver.gsd.RECORD_SEPARATOR)[0])
        self.assertRegex(frame["item"]["toolCall"]["arguments"], r"^\[body omitted: unparseable JSON, \d+ bytes\]$")
        self.assertEqual(frame["item"]["toolCall"]["name"], "search")
        self.assertNotIn("cut-secret-4", driver.gsd.scrub(['{"apiKey": "cut-secret-4", "n'])[0])

    def test_json_behind_an_invisible_prefix_records_no_credential(self) -> None:
        """A byte-order mark, zero-width or word-joiner character or NUL before JSON makes json.loads fail: complete and
        cut bodies, frames and serialized tool arguments still reach evidence without their credential."""
        for prefix in ("﻿", "​", "⁠", "\x00", "﻿ ​"):
            with self.subTest(prefix=repr(prefix)), tempfile.TemporaryDirectory() as directory:
                complete = json.dumps({"password": "bom-secret-1", "q": "x"})
                arguments = {"type": 2, "item": {"toolCall": {"name": "search", "arguments": prefix + complete}}}
                cut_arguments = {
                    "type": 2,
                    "item": {"toolCall": {"arguments": prefix + '{"password": "bom-secret-2", "q'}},
                }
                frames = prefix + json.dumps(arguments) + "\x1e" + json.dumps(cut_arguments) + "\x1e"
                answer = (prefix + json.dumps({"accessToken": "bom-secret-3"})).encode("utf-8")
                evidence = driver.Evidence(Path(directory))
                evidence.at("model-a")
                client = driver.Client("http://127.0.0.1:1", evidence)
                with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, answer)):
                    client.request("POST", "/api/local/v1/chat/hub?id=bom-secret-4", prefix + complete)
                    client.request("POST", "/api/local/v1/chat/hub?id=x", prefix + '{"password": "bom-secret-5", "q')
                    client.request("POST", "/api/local/v1/chat/hub?id=x", frames)
                text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
                self.assertNotRegex(text, r"bom-secret-")
                whole, cut, sent = (json.loads(line) for line in text.splitlines())
                self.assertEqual(json.loads(whole["request"]), {"password": "(redacted)", "q": "x"})
                self.assertEqual(whole["response"], '{"accessToken": "(redacted)"}')
                self.assertRegex(cut["request"], r"^\[body omitted: unparseable JSON, \d+ bytes\]$")
                first, second, rest = sent["request"].split("\x1e")
                self.assertEqual(rest, "")
                kept = json.loads(first)["item"]["toolCall"]
                self.assertEqual(
                    (kept["name"], json.loads(kept["arguments"])), ("search", {"password": "(redacted)", "q": "x"})
                )
                self.assertRegex(
                    json.loads(second)["item"]["toolCall"]["arguments"], r"^\[body omitted: unparseable JSON,"
                )
                self.assertEqual(driver.gsd.scrub([prefix + "-----BEGIN PRIVATE KEY-----"]), ["(redacted)"])

    def test_every_query_value_is_masked_whatever_its_name_or_spelling(self) -> None:
        """Relative, API-prefixed and absolute paths, in a logged path and inside other text."""
        for path, logged in (
            ("chat/hub?ID=q-secret-1", "chat/hub?ID=(redacted)"),
            ("chat/hub?%69d=q-secret-2", "chat/hub?id=(redacted)"),
            ("chat/hub?Id=q-secret-3&foo=q-secret-4", "chat/hub?Id=(redacted)&foo=(redacted)"),
            ("/api/local/v1/chat/hub?a%3Dq-secret-5", "/api/local/v1/chat/hub?(redacted)"),
            ("agents?q-secret-6", "agents?(redacted)"),
            ("agents?x=q secret-7", "agents?x=(redacted)"),
            ("http://u:q-secret-8@127.0.0.1:1/x?ID=q-secret-9#f", "http://(redacted)@127.0.0.1:1/x?ID=(redacted)"),
        ):
            with self.subTest(path):
                self.assertEqual(driver.gsd.redact_path(path), logged)
                in_text = driver.gsd.redact(f"Missing {path.replace(' ', '')}", "agents")
                self.assertNotRegex(in_text, r"secret-")

    def test_secret_path_segments_are_derived_from_the_route_templates(self) -> None:
        """Every LocalApiRoutes template parameter whose name is_secret_key matches names the segment after a prefix in
        SECRET_SEGMENT_PREFIXES, and no other prefix is listed; one behind another parameter needs a new rule."""
        routes = sorted(
            (MODULE_PATH.parents[1] / "XE-Local-AI-Engine.Client/Endpoints/Common").glob("LocalApiRoutes*.cs")
        )
        templates = [t for f in routes for t in re.findall(r'const string \w+ = "([^"]*)";', f.read_text("utf-8"))]
        self.assertGreater(len(templates), 100, "no route templates found")
        derived = set()
        for template in templates:
            segments = template.split("/")
            for index, segment in enumerate(segments):
                parameter = re.fullmatch(r"\{(\w+)(?::[^}]*)?\??\}", segment)
                if parameter and driver.gsd.is_secret_key(parameter[1]):
                    derived.add("/".join(segments[:index]))
        self.assertEqual(derived, set(driver.gsd.SECRET_SEGMENT_PREFIXES))
        self.assertFalse([prefix for prefix in derived if "{" in prefix])

    def test_a_skill_preview_resource_read_records_no_import_token(self) -> None:
        """A successful and a failed read through the real NodeClient.request, whose 404 echoes the URL: the token is
        masked in each logged path and driver error, a spelling the mask misses hides the whole path, and the bodies
        are omitted."""
        token = "preview-secret-token-1"  # noqa: S105  # a sentinel, not a credential
        resource = f"/skills/import/preview/{token}/skills/s1/resources/notes.md"
        failed: list[str] = []
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            fake = self.fake_node({("GET", resource): {"content": "notes"}})
            with mock.patch.object(driver.gsd.urllib.request.OpenerDirector, "open", side_effect=fake):
                self.assertEqual(client.get(resource), {"content": "notes"})
                for path in (
                    f"/Skills/Import/Preview/{token}/skills/s1/resources/x",
                    f"/%73kills/import/preview/{token}/x",
                ):
                    with self.assertRaises(driver.gsd.DriverError) as raised:
                        client.get(path)
                    failed.append(str(raised.exception))
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for recorded in (text, *failed):
            self.assertNotIn(token, recorded)
        logged = [json.loads(line) for line in text.splitlines()]
        self.assertEqual(
            [record["path"] for record in logged],
            [
                "/api/local/v1/skills/import/preview/(redacted)/skills/s1/resources/notes.md",
                "/api/local/v1/Skills/Import/Preview/(redacted)/skills/s1/resources/x",
                "(redacted)",
            ],
        )
        self.assertRegex(logged[0]["response"], r"^\[body omitted: credential-bearing route, \d+ bytes\]$")
        self.assertIn("HTTP 404: [body omitted: credential-bearing route,", failed[0])

    def test_a_skill_preview_link_inside_a_conversation_records_no_import_token(self) -> None:
        """A tool result on a conversation route links a preview resource: the canonical spelling is masked in place;
        any spelling the mask misses, in a JSON string, a JSON-in-string tool result, a plain-text 404 body and the
        driver error, replaces its containing string by a marker."""
        token = "preview-link-token-1"  # noqa: S105  # a sentinel, not a credential
        hidden = (
            f"http://127.0.0.1:1/api/local/v1/%73kills/import/preview/{token}/skills/s1/resources/a.md",
            f"see (/api/local/v1/skills%2Fimport%2Fpreview%2F{token}/x?y=1)",
            f"SKILLS/./Import/PREVIEW/{token.upper()}/x",
            f"skills\\\\import\\\\preview\\\\{token}",
            f"%2573kills/import/preview/{token}?",
        )
        canonical = f"notes: /api/local/v1/Skills/Import/Preview/{token}/skills/s1/resources/notes.md."
        answer = {
            "messages": [
                {"role": "tool", "content": text} for text in (*hidden, canonical, json.dumps({"link": hidden[0]}))
            ]
        }
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, answer)):
                client.get("/chat/conversations/c1")
            failed = []
            for body in hidden:
                error = urllib.error.HTTPError(
                    "http://x", 404, "Not Found", email.message.Message(), io.BytesIO(body.encode())
                )
                self.addCleanup(error.close)
                with (
                    mock.patch.object(driver.gsd.urllib.request.OpenerDirector, "open", side_effect=error),
                    self.assertRaises(driver.gsd.DriverError) as raised,
                ):
                    client.get("/chat/conversations/c2")
                failed.append(str(raised.exception))
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for recorded in (text, *failed):
            self.assertNotIn(token, recorded.lower())
        contents = [message["content"] for message in json.loads(text.splitlines()[0])["response"]["messages"]]
        self.assertEqual(
            len(hidden),
            sum(bool(re.fullmatch(r"\[body omitted: unmaskable secret path, \d+ bytes\]", c)) for c in contents),
            contents,
        )
        self.assertIn("notes: /api/local/v1/Skills/Import/Preview/(redacted)/skills/s1/resources/notes.md.", contents)
        self.assertEqual(json.loads(contents[-1])["link"][:15], "[body omitted: ")
        self.assertTrue(all("HTTP 404: [body omitted: unmaskable secret path," in error for error in failed), failed)

    def test_a_url_loses_its_userinfo_and_query_values(self) -> None:
        """An MCP URL, or any other absolute URL, on a route outside the credential families: path and body."""
        answer = {
            "items": [
                {"name": "docs", "url": "https://user:url-secret-1@mcp.example/sse?token=url-secret-2&m=x"},
                {"name": "mail", "url": "https://ops.lab@mail.example:url-secret-5@mcp.example/sse"},
            ]
        }
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with mock.patch.object(driver.gsd.NodeClient, "request", return_value=(200, answer)):
                client.get("/tool-catalog?source=https://u:url-secret-3@mcp.example/x?sig=url-secret-4")
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for secret in (f"url-secret-{n}" for n in range(1, 6)):
            self.assertNotIn(secret, text)
        record = json.loads(text)
        self.assertEqual(record["path"], "/api/local/v1/tool-catalog?source=(redacted)")
        self.assertEqual(
            record["response"]["items"],
            [
                {"name": "docs", "url": "https://(redacted)@mcp.example/sse?token=(redacted)&m=(redacted)"},
                {"name": "mail", "url": "https://(redacted)@mcp.example/sse"},
            ],
        )

    def test_an_ambiguous_authority_masks_the_whole_url(self) -> None:
        """An "@" after a path delimiter (a parser could end the userinfo there) or unbalanced host brackets: the
        authority, path and fragment become one marker; the query is masked as usual."""
        for url, logged in (
            ("https://u:amb-secret-1/x@host/p", "https://(redacted)"),
            ("https://u:amb-secret-2#x@host/p", "https://(redacted)"),
            ("https://host/p/@amb-secret-3?k=amb-secret-4", "https://(redacted)?k=(redacted)"),
            ("https://[u:amb-secret-5@host]/p", "https://(redacted)"),
            ("https://u@[::1/amb-secret-6", "https://(redacted)"),
            ("https://u:amb-secret-7@[::1]:8080/p", "https://(redacted)@[::1]:8080/p"),
        ):
            with self.subTest(url):
                self.assertEqual(driver.gsd.redact_path(url), logged)
                self.assertNotIn("amb-secret", driver.gsd.redact(f"see {url} now", "agents"))

    TOKEN, CONNECTION_ID = "hub-secret-token-1", "hub-secret-id-2"

    def fake_node(self, routes: dict[tuple[str, str], object] | None = None):
        """A stand-in for the opener's open: a node whose hub negotiates the two sentinels, accepts POSTs, answers every
        poll with a 404 whose body echoes the polled URL (connection token included), and refuses the close."""
        token, connection_id = self.TOKEN, self.CONNECTION_ID

        class Answer:
            def __init__(self, body: bytes) -> None:
                self.body, self.status = body, 200

            def __enter__(self) -> Answer:
                return self

            def __exit__(self, *_exc: object) -> None:
                return None

            def read(self) -> bytes:
                return self.body

        def open_url(request, **_kwargs):
            method, url = request.get_method(), request.full_url
            for (route_method, suffix), answer in (routes or {}).items():
                if method == route_method and url.endswith(suffix):
                    return Answer(json.dumps(answer).encode())
            if url.endswith("/negotiate?negotiateVersion=1"):
                negotiate = {
                    "connectionToken": token,
                    "connectionId": connection_id,
                    "availableTransports": [{"transport": "LongPolling"}],
                }
                return Answer(json.dumps(negotiate).encode())
            if method == "POST":
                return Answer(b"")
            if method == "GET":
                body = io.BytesIO(f"Missing {url}".encode())
                error = urllib.error.HTTPError(url, 404, "Not Found", email.message.Message(), body)
                self.addCleanup(error.close)
                raise error
            raise urllib.error.URLError("connection refused")

        return open_url

    def assert_no_hub_secret(self, text: str) -> None:
        for leaked in (self.TOKEN, self.CONNECTION_ID):
            self.assertNotIn(leaked, text)

    def test_a_hub_turn_records_no_connection_token_even_when_its_poll_fails(self) -> None:
        """Negotiate, handshake, invocation, a failed poll and the failed close through the real NodeClient.request;
        only the opener's open is faked."""
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with (
                mock.patch.object(driver.gsd.urllib.request.OpenerDirector, "open", side_effect=self.fake_node()),
                self.assertRaises(driver.gsd.DriverError) as raised,
                driver.gsd.HubStream(client, "/api/local/v1/chat/hub") as stream,
            ):
                list(stream.invoke_stream("Chat", [], 30))
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        logged = [json.loads(line) for line in text.splitlines()]
        self.assertEqual(
            [(r["method"], "error" in r) for r in logged],
            [("POST", False), ("POST", False), ("POST", False), ("GET", True), ("DELETE", True)],
        )
        self.assertIn("HTTP 404: Missing http://127.0.0.1:1/api/local/v1/chat/hub?id=(redacted)", logged[3]["error"])
        self.assert_no_hub_secret(text)
        self.assert_no_hub_secret(str(raised.exception))

    def test_a_hub_invocation_records_its_frame_with_credentials_scrubbed(self) -> None:
        """The SendMessage POST is a SignalR frame ending in the record separator, not a JSON document."""
        body = {
            "conversationId": "c-1",
            "content": json.dumps({"password": "frame-secret-1"}),
            "apiKey": "frame-secret-2",
        }
        with tempfile.TemporaryDirectory() as directory:
            evidence = driver.Evidence(Path(directory))
            evidence.at("model-a")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with (
                mock.patch.object(driver.gsd.urllib.request.OpenerDirector, "open", side_effect=self.fake_node()),
                self.assertRaises(driver.gsd.DriverError),
                driver.gsd.HubStream(client, "/api/local/v1/chat/hub") as stream,
            ):
                list(stream.invoke_stream("SendMessage", [body], 30))
            text = (Path(directory) / "model-a" / "http.jsonl").read_text(encoding="utf-8")
        for leaked in ("frame-secret-1", "frame-secret-2"):
            self.assertNotIn(leaked, text)
        invocation = json.loads(text.splitlines()[2])["request"]
        frame, rest = invocation.split(driver.gsd.RECORD_SEPARATOR)
        self.assertEqual(rest, "")
        sent = json.loads(frame)
        self.assertEqual(sent["target"], "SendMessage")
        self.assertEqual(sent["arguments"][0]["conversationId"], "c-1")
        self.assertEqual(sent["arguments"][0]["apiKey"], "(redacted)")
        self.assertEqual(json.loads(sent["arguments"][0]["content"]), {"password": "(redacted)"})

    def test_a_failed_hub_poll_leaves_no_connection_token_in_the_model_evidence(self) -> None:
        """One model's thinking-off check through run_model: the failed turn lands in turns.jsonl and checks.jsonl."""
        entry = {"id": "model-a", "modelName": "m", "thinkingOff": "template"}
        routes: dict[tuple[str, str], object] = {
            ("GET", "/models"): {"items": [{"modelName": "m"}]},
            ("GET", "/model-fit/running"): {"items": []},
            ("GET", "/node-settings"): {},
            ("POST", "/chat/conversations"): {"conversationId": "c-1"},
        }
        with tempfile.TemporaryDirectory() as directory:
            models = Path(directory) / "models"
            models.mkdir()
            (models / "m.gguf.xe-model.json").write_text(json.dumps({"ModelName": "m", "LocalFileName": "m.gguf"}))
            evidence = driver.Evidence(Path(directory) / "evidence")
            client = driver.Client("http://127.0.0.1:1", evidence)
            with (
                mock.patch.object(driver.gsd.urllib.request.OpenerDirector, "open", side_effect=self.fake_node(routes)),
                mock.patch.object(driver, "SlotWatcher"),
            ):
                result = driver.run_model(client, evidence, entry, models, "cpu", ["thinking-off"])
            written = {
                p.name: p.read_text(encoding="utf-8") for p in (Path(directory) / "evidence" / "model-a").iterdir()
            }
        self.assertEqual(result["checks"]["thinking-off"]["verdict"], "fail")
        self.assertIn("HTTP 404: Missing", written["checks.jsonl"])
        for name in ("checks.jsonl", "http.jsonl", "turns.jsonl"):
            self.assert_no_hub_secret(written[name])


if __name__ == "__main__":
    unittest.main()
