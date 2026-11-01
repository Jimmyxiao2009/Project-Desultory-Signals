# -*- coding: utf-8 -*-
"""Determinism primitives.

Signing must be reproducible: given the same inputs and the same clock, the
same ``a_bogus`` must come out. Otherwise the desktop unit tests cannot pin the
algorithm and a device failure cannot be replayed. So this layer never calls
``time.time()`` or ``random`` directly -- it takes a :class:`Clock`.

``Clock`` is also the seam for the host: the C# side constructs it with the
device's real time and a real PRNG; the test runner constructs it with a fixed
seed and fixed timestamp.
"""

import os
import random
import time


class Clock(object):
    """Injectable wall clock + PRNG.

    ``seed`` makes the PRNG reproducible. When ``seed`` is None a system seed is
    used, which is what production wants.
    """

    __slots__ = ("_fixed_now_ms", "_rng", "seed")

    def __init__(self, seed=None, fixed_now_ms=None):
        self.seed = seed
        self._fixed_now_ms = fixed_now_ms
        self._rng = random.Random(seed) if seed is not None else random.Random()

    # -- time ---------------------------------------------------------------
    def now_ms(self):
        if self._fixed_now_ms is not None:
            return self._fixed_now_ms
        return int(time.time() * 1000)

    def now_s(self):
        return self.now_ms() // 1000

    def advance_ms(self, delta):
        """Test helper: move a fixed clock forward."""
        if self._fixed_now_ms is None:
            raise RuntimeError("advance_ms requires a fixed clock")
        self._fixed_now_ms += int(delta)

    # -- randomness --------------------------------------------------------
    def randbytes(self, count):
        """Cryptographic bytes when available, else the seeded PRNG.

        The embedded runtime supports ``os.urandom`` (the static core links
        ``posixmodule``); the seeded path keeps desktop tests deterministic.
        """
        if self.seed is not None:
            return bytes(bytearray(self._rng.randrange(0, 256) for _ in range(count)))
        try:
            return os.urandom(count)
        except Exception:
            return bytes(bytearray(self._rng.randrange(0, 256) for _ in range(count)))

    def randint(self, low, high):
        return self._rng.randint(low, high)

    def random_float(self):
        return self._rng.random()

    def hex(self, count):
        """``count`` random lowercase hex characters."""
        from douyin.util import to_text

        from binascii import hexlify

        raw = self.randbytes((count + 1) // 2)
        return to_text(hexlify(raw))[:count]


def system_clock():
    return Clock()
