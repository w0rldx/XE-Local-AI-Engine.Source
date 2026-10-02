"""Unit tests for the stdlib-only helpers in scripts/gpu-smoke-driver.py.

`unittest.TestCase` because two runners execute this file: pytest (python-quality) and
scripts/run-release-contract-tests.sh as a bare `python3 <file>`. The subject's filename is not a
valid module name, so it is loaded through importlib. No network: the negative-control verdict is
driven by patching NodeClient.request.
"""

from __future__ import annotations

import argparse
import contextlib
import email.parser
import importlib.util
import io
import struct
import sys
import unittest
import zlib
from pathlib import Path
from unittest import mock

MODULE_PATH = Path(__file__).resolve().parents[2] / "scripts" / "gpu-smoke-driver.py"
SPEC = importlib.util.spec_from_file_location("gpu_smoke_driver", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise ImportError(MODULE_PATH)
driver = importlib.util.module_from_spec(SPEC)
sys.modules["gpu_smoke_driver"] = driver
SPEC.loader.exec_module(driver)


def records(output: str) -> dict[str, str]:
    return dict(line.split("\t", 1) for line in output.splitlines() if "\t" in line)


class PngFixtureTests(unittest.TestCase):
    def test_signature_ihdr_and_pixels(self) -> None:
        data = driver.png_bytes(256, 256)
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        length, kind = struct.unpack(">I4s", data[8:16])
        self.assertEqual((length, kind), (13, b"IHDR"))
        width, height, depth, colour = struct.unpack(">IIBB", data[16:26])
        self.assertEqual((width, height, depth, colour), (256, 256, 8, 2))
        ihdr_crc = struct.unpack(">I", data[29:33])[0]
        self.assertEqual(ihdr_crc, zlib.crc32(data[12:29]))
        idat_length = struct.unpack(">I", data[33:37])[0]
        rows = zlib.decompress(data[41 : 41 + idat_length])
        self.assertEqual(len(rows), 256 * (1 + 256 * 3))
        self.assertTrue(data.endswith(b"IEND\xaeB`\x82"))
        # A gradient, not a flat colour: the first and last pixel of the first row differ.
        self.assertNotEqual(rows[1:4], rows[1 + 255 * 3 : 4 + 255 * 3])


class MultipartTests(unittest.TestCase):
    def test_round_trips_through_a_mime_parser(self) -> None:
        payload = b"\x89PNG\r\n--not-a-boundary\r\n\x00\xff"
        body, header = driver.encode_multipart("file", "edit-source.png", "image/png", payload)
        self.assertTrue(header.startswith("multipart/form-data; boundary="))
        message = email.parser.BytesParser().parsebytes(b"Content-Type: " + header.encode() + b"\r\n\r\n" + body)
        parts = [part for part in message.walk() if not part.is_multipart()]
        self.assertEqual(len(parts), 1)
        part = parts[0]
        self.assertEqual(part.get_param("name", header="content-disposition"), "file")
        self.assertEqual(part.get_filename(), "edit-source.png")
        self.assertEqual(part.get_content_type(), "image/png")
        self.assertEqual(part.get_payload(decode=True), payload)


class NegativeControlTests(unittest.TestCase):
    def run_negative(self, outcome: object) -> dict[str, str]:
        args = argparse.Namespace(
            base_url="http://127.0.0.1:1",
            token="t",  # noqa: S106  # fake bearer, the request is patched
            timeout=1.0,
            model="m",
            source="up-1",
            mode="reference",
            strength=None,
            prompt="p",
            width=256,
            height=256,
            steps=8,
            seed=42,
        )
        side_effect = outcome if isinstance(outcome, Exception) else None
        with mock.patch.object(driver.NodeClient, "request", side_effect=side_effect, return_value=outcome):
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                self.assertEqual(driver.command_image_edit_negative(args), 0)
        return records(buffer.getvalue())

    def test_4xx_is_refused_and_keeps_the_message(self) -> None:
        result = self.run_negative((400, {"errors": [{"reason": "does not support this edit mode."}]}))
        self.assertEqual(result["negative"], "refused")
        self.assertEqual(result["httpStatus"], "400")
        self.assertIn("does not support this edit mode", result["message"])

    def test_2xx_is_accepted(self) -> None:
        self.assertEqual(self.run_negative((201, {"id": "j"}))["negative"], "accepted")

    def test_transport_or_5xx_is_error(self) -> None:
        self.assertEqual(self.run_negative(driver.DriverError("HTTP 500"))["negative"], "error")

    def test_edit_body_sends_strength_only_when_given(self) -> None:
        args = argparse.Namespace(
            model="m", prompt="p", width=1, height=1, steps=1, seed=1, mode="img2img", source="up-1", strength=None
        )
        self.assertNotIn("strength", driver.edit_body(args))
        args.strength = 0.6
        body = driver.edit_body(args)
        self.assertEqual((body["strength"], body["editMode"], body["sourceImageId"]), (0.6, "img2img", "up-1"))


if __name__ == "__main__":
    unittest.main()
