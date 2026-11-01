# -*- coding: utf-8 -*-
# VENDORED REFERENCE COPY -- not part of the live signing path.
#
# Origin : _research/abogus/abogus.py (this repository)
# Evidence: _research/abogus/EVIDENCE.md
# Copied : 2026-10-07
#
# This module is a pure-standard-library reverse-engineering artifact produced
# against ByteDance's live ``bdms.js`` (sha256(first16) a0e46f84476d63a1). It is
# vendored here so the signature work sits next to the code that will consume it,
# and so it is covered by the same stdlib-only constraint (it imports only
# ``random``/``sys``/``time``, which the ARM32 UWP runtime has).
#
# STATUS: PARTIAL. The *encoder* half is verified against six captured real
# signatures (``current_encode`` + ``KEYSTREAM`` reproduce them bit-exactly). The
# *payload* half is not: ``build_current_payload`` raises IncompleteReverseError
# naming the offsets that remain unsolved. Do not register a provider on top of it
# until that raises no more.
#
# Why this does not block the client: as of the 2026-10-07 measurement the live
# gate is a server-issued ``ttwid`` cookie, not a valid ``a_bogus``, and sending a
# signature where it is not wanted *causes* a 403. See ``endpoints.SIGNATURE_POLICY``.
#
# The original module's own docstring follows, unmodified.
#
# -*- coding: utf-8 -*-
"""Douyin web ``a_bogus`` signer -- pure Python, standard library only.

Target runtime: CPython 3.15 *static* core on ARM32 UWP, no ``_ssl``/``socket``,
no third-party packages.  Everything here therefore uses only ``time``,
``random``, ``hashlib``-free integer arithmetic and conservative syntax (works on
3.13).  No big-integer loops.

WHAT IS IN THIS FILE
--------------------
1. ``sm3()``                      SM3 (GB/T 32905-2016), pure integers, KAT-tested.
2. ``rc4()`` / ``b64_custom()``   the two other primitives of the bdms pipeline.
3. ``perm_cipher()``              the fixed 256-byte permutation stream cipher.
4. ``a_bogus_legacy()``           the *published* bdms algorithm (v0.0.3 line of
                                  ``Johnserf-Seed/f2``).  Complete and
                                  deterministic, but see the warning below.
5. ``CURRENT_*`` + ``build_current_payload()``
                                  everything that has been *verified* about the
                                  algorithm the live ``bdms.js`` actually runs
                                  (release ``1.0.1.19-fix.01``), plus an explicit
                                  list of the slots that are still unknown.
6. ``a_bogus()``                  the public entry point required by the spec.

READ THIS BEFORE USING IT
-------------------------
``a_bogus_legacy()`` reproduces the algorithm that every public Python port
implements.  It was **empirically disproved** against the live asset: decoding a
real ``a_bogus`` produced by the current ``bdms.js`` does not invert to the
legacy payload structure (see ``EVIDENCE.md`` section 4).  The live generator is
a 796-program stack VM with a *different* payload permutation and a different
cipher, so ``a_bogus_legacy`` output is **not** a valid current ``a_bogus``.

The live pipeline *frame* has however been verified bit-exactly:

    a_bogus = b64_custom( PREFIX4 + (PAYLOAD xor KEYSTREAM), ALPHABET_255 )

with ``PREFIX4`` a 4-byte nonce, ``PAYLOAD`` a 134-byte buffer and
``KEYSTREAM`` a fixed 134-byte constant (both extracted from the live asset,
see ``KEYSTREAM`` below and ``EVIDENCE.md`` section 5).  ``build_current_payload``
assembles every ``PAYLOAD`` slot that has already been solved and raises
``IncompleteReverseError`` naming the exact byte offsets that are still open.

The good news for the UWP client is in ``EVIDENCE.md`` section 6: as of
2026-10-07 the douyin web API does **not** reject requests with an absent or
garbage ``a_bogus``; the actual gate is the ``ttwid``/``uidid`` cookie.  So this
module is not on the critical path today -- it exists so that the client can
switch it on when Argus hardens.
"""

import random
import sys
import time

# ==========================================================================
# 1. SM3
# ==========================================================================

_SM3_IV = (0x7380166F, 0x4914B2B9, 0x172442D7, 0xDA8A0600,
           0xA96F30BC, 0x163138AA, 0xE38DEE4D, 0xB0FB0E4E)
_M32 = 0xFFFFFFFF


def _rotl32(x, n):
    n &= 31
    return ((x << n) | (x >> (32 - n))) & _M32


def _sm3_p0(x):
    return x ^ _rotl32(x, 9) ^ _rotl32(x, 17)


def _sm3_p1(x):
    return x ^ _rotl32(x, 15) ^ _rotl32(x, 23)


def _sm3_block(v, blk):
    w = [0] * 68
    for i in range(16):
        j = i * 4
        w[i] = (blk[j] << 24) | (blk[j + 1] << 16) | (blk[j + 2] << 8) | blk[j + 3]
    for i in range(16, 68):
        w[i] = (_sm3_p1(w[i - 16] ^ w[i - 9] ^ _rotl32(w[i - 3], 15))
                ^ _rotl32(w[i - 13], 7) ^ w[i - 6]) & _M32
    w1 = [w[i] ^ w[i + 4] for i in range(64)]

    a, b, c, d, e, f, g, h = v
    for j in range(64):
        if j < 16:
            tj = 0x79CC4519
            ff = a ^ b ^ c
            gg = e ^ f ^ g
        else:
            tj = 0x7A879D8A
            ff = (a & b) | (a & c) | (b & c)
            gg = (e & f) | ((~e & _M32) & g)
        ss1 = _rotl32((_rotl32(a, 12) + e + _rotl32(tj, j)) & _M32, 7)
        ss2 = ss1 ^ _rotl32(a, 12)
        tt1 = (ff + d + ss2 + w1[j]) & _M32
        tt2 = (gg + h + ss1 + w[j]) & _M32
        d = c
        c = _rotl32(b, 9)
        b = a
        a = tt1
        h = g
        g = _rotl32(f, 19)
        f = e
        e = _sm3_p0(tt2)
    v[0] ^= a
    v[1] ^= b
    v[2] ^= c
    v[3] ^= d
    v[4] ^= e
    v[5] ^= f
    v[6] ^= g
    v[7] ^= h


def sm3(data):
    """SM3 digest (32 bytes).  ``data`` is str/bytes or a sequence of ints."""
    if isinstance(data, str):
        data = data.encode("utf-8")
    elif not isinstance(data, (bytes, bytearray)):
        data = bytes(bytearray((x & 0xFF) for x in data))
    msg = bytearray(data)
    bit_len = len(msg) * 8
    msg.append(0x80)
    while len(msg) % 64 != 56:
        msg.append(0x00)
    msg += bit_len.to_bytes(8, "big")
    v = list(_SM3_IV)
    for off in range(0, len(msg), 64):
        _sm3_block(v, msg[off:off + 64])
    out = bytearray()
    for word in v:
        out += word.to_bytes(4, "big")
    return bytes(out)


# ==========================================================================
# 2. other primitives
# ==========================================================================

#: 256-byte permutation used by the legacy stream cipher (a permutation of 0..255).
BIG_ARRAY = [
    121, 243, 55, 234, 103, 36, 47, 228, 30, 231, 106, 6, 115, 95, 78, 101,
    250, 207, 198, 50, 139, 227, 220, 105, 97, 143, 34, 28, 194, 215, 18, 100,
    159, 160, 43, 8, 169, 217, 180, 120, 247, 45, 90, 11, 27, 197, 46, 3,
    84, 72, 5, 68, 62, 56, 221, 75, 144, 79, 73, 161, 178, 81, 64, 187,
    134, 117, 186, 118, 16, 241, 130, 71, 89, 147, 122, 129, 65, 40, 88, 150,
    110, 219, 199, 255, 181, 254, 48, 4, 195, 248, 208, 32, 116, 167, 69, 201,
    17, 124, 125, 104, 96, 83, 80, 127, 236, 108, 154, 126, 204, 15, 20, 135,
    112, 158, 13, 1, 188, 164, 210, 237, 222, 98, 212, 77, 253, 42, 170, 202,
    26, 22, 29, 182, 251, 10, 173, 152, 58, 138, 54, 141, 185, 33, 157, 31,
    252, 132, 233, 235, 102, 196, 191, 223, 240, 148, 39, 123, 92, 82, 128, 109,
    57, 24, 38, 113, 209, 245, 2, 119, 153, 229, 189, 214, 230, 174, 232, 63,
    52, 205, 86, 140, 66, 175, 111, 171, 246, 133, 238, 193, 99, 60, 74, 91,
    225, 51, 76, 37, 145, 211, 166, 151, 213, 206, 0, 200, 244, 176, 218, 44,
    184, 172, 49, 216, 93, 168, 53, 21, 183, 41, 67, 85, 224, 155, 226, 242,
    87, 177, 146, 70, 190, 12, 162, 19, 137, 114, 25, 165, 163, 192, 23, 59,
    9, 94, 179, 107, 35, 7, 142, 131, 239, 203, 149, 136, 61, 249, 14, 156,
]

#: custom base64 alphabets, verbatim from the live bdms.js VM string table
#: (indices 249/251/253/255 in vm_Z.json dumped out of the live asset).
ALPHABET_249 = "Dkdpgh4ZKsQB80/Mfvw36XI1R25+WUAlEi7NLboqYTOPuzmFjJnryx9HVGcaStCe="
ALPHABET_251 = "Dkdpgh4ZKsQB80/Mfvw36XI1R25-WUAlEi7NLboqYTOPuzmFjJnryx9HVGcaStCe="
ALPHABET_253 = "ckdp1h4ZKsUB80/Mfvw36XIgR25+WQAlEi7NLboqYTOPuzmFjJnryx9HVGDaStCe"
ALPHABET_255 = "Dkdpgh2ZmsQB80/MfvV36XI1R45-WUAlEixNLwoqYTOPuzKFjJnry79HbGcaStCe"

#: the output alphabet of the LIVE generator (verified: every character of real
#: a_bogus values is in this set, and encoding with it reproduces them exactly).
OUT_ALPHABET = ALPHABET_255
#: the alphabet used inside the UA sub-hash (legacy pipeline).
UA_ALPHABET = ALPHABET_253
UA_KEY = b"\x00\x01\x0E"

SALT = "dhzx"     #: SM3 salt, live VM string table index 262
PAGE_ID = 6241    #: pageId handed to bdms.init by sdk-glue
AID = 6383        #: douyin web aid


def rc4(key, text):
    """RC4 over the code points of ``text`` (bdms works on code units, not bytes)."""
    s = list(range(256))
    j = 0
    klen = len(key)
    for i in range(256):
        j = (j + s[i] + key[i % klen]) & 0xFF
        s[i], s[j] = s[j], s[i]
    i = j = 0
    out = bytearray()
    for ch in text:
        i = (i + 1) & 0xFF
        j = (j + s[i]) & 0xFF
        s[i], s[j] = s[j], s[i]
        out.append((ord(ch) ^ s[(s[i] + s[j]) & 0xFF]) & 0xFF)
    return bytes(out)


def b64_custom(raw, alphabet):
    """Group-of-3 -> group-of-4 base64 with a custom 64-character alphabet."""
    out = []
    for i in range(0, len(raw), 3):
        chunk = raw[i:i + 3]
        v = 0
        for b in chunk:
            v = (v << 8) | b
        v <<= 8 * (3 - len(chunk))
        for shift in (18, 12, 6, 0):
            out.append(alphabet[(v >> shift) & 0x3F])
        for k in range(3 - len(chunk)):
            out[-1 - k] = "="
    return "".join(out)


def b64_custom_decode(text, alphabet):
    idx = {}
    for i, c in enumerate(alphabet[:64]):
        idx[c] = i
    out = bytearray()
    acc = 0
    bits = 0
    for ch in text:
        if ch == "=":
            break
        acc = (acc << 6) | idx[ch]
        bits += 6
        if bits >= 8:
            bits -= 8
            out.append((acc >> bits) & 0xFF)
    return bytes(out)


def perm_keystream(length, big_array=None):
    """Keystream of the legacy permutation cipher (data independent)."""
    arr = list(BIG_ARRAY if big_array is None else big_array)
    n = len(arr)
    out = bytearray(length)
    index_b = arr[1]
    initial_value = 0
    value_e = 0
    for index in range(length):
        if index == 0:
            initial_value = arr[index_b]
            sum_initial = index_b + initial_value
            arr[1] = initial_value
            arr[index_b] = index_b
        else:
            sum_initial = initial_value + value_e
        sum_initial %= n
        out[index] = arr[sum_initial]
        value_e = arr[(index + 2) % n]
        sum_initial = (index_b + value_e) % n
        initial_value = arr[sum_initial]
        arr[sum_initial] = arr[(index + 2) % n]
        arr[(index + 2) % n] = initial_value
        index_b = sum_initial
    return bytes(out)


def perm_cipher(values, big_array=None):
    ks = perm_keystream(len(values), big_array)
    return bytes((v ^ k) & 0xFF for v, k in zip(values, ks))


def perm_uncipher(cipher, big_array=None):
    ks = perm_keystream(len(cipher), big_array)
    return bytes((c ^ k) & 0xFF for c, k in zip(cipher, ks))


# ==========================================================================
# 3. legacy algorithm (complete, but NOT what the live asset runs)
# ==========================================================================

SORT_INDEX = [18, 20, 52, 26, 30, 34, 58, 38, 40, 53, 42, 21, 27, 54, 55, 31,
              35, 57, 39, 41, 43, 22, 28, 32, 60, 36, 23, 29, 33, 37, 44, 45,
              59, 46, 47, 48, 49, 50, 24, 25, 65, 66, 70, 71]
SORT_INDEX_2 = [18, 20, 26, 30, 34, 38, 40, 42, 21, 27, 31, 35, 39, 41, 43, 22,
                28, 32, 36, 23, 29, 33, 37, 44, 45, 46, 47, 48, 49, 50, 24, 25,
                52, 53, 54, 55, 57, 58, 59, 60, 65, 66, 70, 71]
OPTIONS_GET = (0, 1, 8)
OPTIONS_POST = (0, 1, 14)
DEFAULT_FINGERPRINT = "1920|1080|1948|1170|0|0|0|0|1920|1080|1920|1040|1920|1080|24|24|Win32"
DEFAULT_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0")


def _hash2(text, salt):
    return sm3(sm3((text + salt).encode("utf-8")))


def ua_hash_legacy(user_agent, salt=SALT):
    inner = rc4(UA_KEY, user_agent)
    b64 = b64_custom(inner, UA_ALPHABET)
    return sm3(b64.encode("latin-1"))


def legacy_payload(params, body, user_agent, *, salt=SALT, aid=AID, page_id=PAGE_ID,
                   fingerprint=DEFAULT_FINGERPRINT, options=OPTIONS_GET,
                   start_ms=0, end_ms=0):
    a1 = _hash2(params, salt)
    a2 = _hash2(body, salt)
    a3 = ua_hash_legacy(user_agent, salt)
    d = {8: 3, 18: 44, 66: 0, 69: 0, 70: 0, 71: 0}
    d[20] = (start_ms >> 24) & 255
    d[21] = (start_ms >> 16) & 255
    d[22] = (start_ms >> 8) & 255
    d[23] = start_ms & 255
    d[24] = int(start_ms / 2 ** 32)
    d[25] = int(start_ms / 2 ** 40)
    d[26] = (options[0] >> 24) & 255
    d[27] = (options[0] >> 16) & 255
    d[28] = (options[0] >> 8) & 255
    d[29] = options[0] & 255
    d[30] = int(options[1] / 256) & 255
    d[31] = (options[1] % 256) & 255
    d[32] = (options[1] >> 24) & 255
    d[33] = (options[1] >> 16) & 255
    d[34] = (options[2] >> 24) & 255
    d[35] = (options[2] >> 16) & 255
    d[36] = (options[2] >> 8) & 255
    d[37] = options[2] & 255
    d[38], d[39] = a1[21], a1[22]
    d[40], d[41] = a2[21], a2[22]
    d[42], d[43] = a3[23], a3[24]
    d[44] = (end_ms >> 24) & 255
    d[45] = (end_ms >> 16) & 255
    d[46] = (end_ms >> 8) & 255
    d[47] = end_ms & 255
    d[48] = d[8]
    d[49] = int(end_ms / 2 ** 32)
    d[50] = int(end_ms / 2 ** 40)
    d[51] = (page_id >> 24) & 255
    d[52] = (page_id >> 16) & 255
    d[53] = (page_id >> 8) & 255
    d[54] = page_id & 255
    d[55] = page_id
    d[56] = aid
    d[57] = aid & 255
    d[58] = (aid >> 8) & 255
    d[59] = (aid >> 16) & 255
    d[60] = (aid >> 24) & 255
    fp = [ord(c) for c in fingerprint]
    d[64] = len(fingerprint)
    d[65] = len(fingerprint)
    values = [(d.get(i, 0)) & 0xFF for i in SORT_INDEX]
    xor = 0
    for i in range(len(SORT_INDEX_2) - 1):
        if i == 0:
            xor = d.get(SORT_INDEX_2[0], 0)
        xor ^= d.get(SORT_INDEX_2[i + 1], 0)
    values.extend(fp)
    values.append(xor & 0xFF)
    return values


def _rand_prefix(words):
    out = bytearray()
    for rd in words:
        b = rd & 0xFF
        out.append(((b & 170) | 1) & 0xFF)
        out.append(((b & 85) | 2) & 0xFF)
        hi = (rd >> 8) & 0xFF
        out.append(((hi & 170) | 5) & 0xFF)
        out.append(((hi & 85) | 40) & 0xFF)
    return bytes(out)


def a_bogus_legacy(params, body, user_agent, *, salt=SALT, aid=AID, page_id=PAGE_ID,
                   fingerprint=DEFAULT_FINGERPRINT, options=OPTIONS_GET,
                   start_ms=None, end_ms=None, rand_words=None):
    """Legacy bdms algorithm.  Deterministic if start_ms/end_ms/rand_words given."""
    if start_ms is None:
        start_ms = int(time.time() * 1000)
    if end_ms is None:
        end_ms = int(time.time() * 1000)
    if rand_words is None:
        rand_words = [int(random.random() * 10000) for _ in range(3)]
    values = legacy_payload(params, body, user_agent, salt=salt, aid=aid,
                            page_id=page_id, fingerprint=fingerprint,
                            options=options, start_ms=start_ms, end_ms=end_ms)
    blob = _rand_prefix(rand_words) + perm_cipher(values)
    return b64_custom(blob, OUT_ALPHABET)


# ==========================================================================
# 4. the CURRENT (live 1.0.1.19-fix.01) frame -- verified
# ==========================================================================

class IncompleteReverseError(NotImplementedError):
    """Raised when the live payload assembly is asked for an unsolved slot."""


#: Fixed 134-byte XOR keystream of the live generator's final cipher.  Verified:
#: for four independent signatures (query / UA / pageId / screen variations) the
#: relation  payload[4+i] == payload_plain[i] ^ KEYSTREAM[i]  holds with exactly
#: the same table, i.e. the cipher is an index-local XOR and this table is a
#: permanent constant of bdms 1.0.1.19-fix.01.  Extracted from the live asset.
KEYSTREAM = [
    124, 32, 32, 103, 57, 169, 137, 173, 34, 160, 126, 208, 194, 157, 195, 125,
    31, 142, 154, 52, 20, 251, 65, 114, 19, 177, 213, 42, 143, 112, 91, 249,
    231, 37, 224, 220, 18, 35, 226, 105, 112, 102, 45, 172, 251, 198, 249, 157,
    100, 183, 80, 3, 157, 160, 32, 170, 44, 246, 244, 239, 125, 194, 156, 128,
    80, 219, 86, 112, 243, 248, 150, 88, 215, 36, 3, 89, 225, 214, 228, 121,
    110, 7, 21, 202, 227, 19, 163, 34, 202, 69, 186, 84, 145, 215, 173, 181,
    197, 100, 109, 227, 214, 42, 50, 175, 38, 12, 181, 72, 7, 199, 232, 219,
    83, 26, 150, 51, 159, 72, 228, 56, 90, 72, 159, 91, 100, 96, 134, 74,
    248, 82, 136, 207, 157, 48,
]

#: Payload length of the live generator for the profiled 1440x900/1920x1080 PC
#: profile.  The length is not fixed: it grows with the decimal width of the
#: geometry values baked into the fingerprint tail (screen 1920x1080 added 4
#: bytes, timestamp 1788091256999 added 2).
CURRENT_PAYLOAD_LEN = 134

#: Slots that carry data derived from the request.  Solved ones are expressed as
#: (index, source, mask, const) with ``value = ((source & mask) ^ const)``.
#: ``source`` is resolved in build_current_payload().
CURRENT_SLOT_RULES = [
    # --- query: H = SM3(SM3(query + "dhzx") + "dhzx") ---
    (20, "Q", 18, 0x6E, 0x11),
    (23, "Q", 18, 0x91, 0x42),
    (28, "Q", 9, 0x6E, 0x80),
    (31, "Q", 9, 0x91, 0x00),
    # --- body: B = SM3(SM3(body + "dhzx") + "dhzx") ---
    (36, "B", 4, 0x6E, 0x81),
    (39, "B", 4, 0x91, 0x02),
    (52, "B", 10, 0x6E, 0x01),
    (58, "B", 19, 0x93, 0x2C),
    (59, "B", 19, 0x28, 0xC0),   # merged with a pageId contribution
    # --- pageId (24-bit) ---
    (25, "P", 1, 0xBD, 0x00),    # (pageId >> 8) & 0xFF
    (27, "P", 1, 0x42, 0x01),
    (57, "P", 0, 0xBD, 0x42),    # pageId & 0xFF
    (59, "P", 0, 0x42, 0xA0),
    (16, "P", 3, 0x02, 0x81),
    (19, "P", 3, 0x10, 0x24),
    (38, "P", 3, 0x10, 0x28),
    # --- aid (constant 6383 for the web app) ---
    (33, "A", 0, 0xBD, 0x42),
    (35, "A", 0, 0x42, 0x34),
    (46, "A", 1, 0x11, 0x00),
    (47, "A", 1, 0x28, 0x12),
    # --- checksum ---
    (133, "CK", 0, 0x00, 0x00),
]

#: Source byte selectors used by CURRENT_SLOT_RULES.
CURRENT_SOURCE_SEL = {
    "P": lambda page_id, aid: [(page_id >> 0) & 255, (page_id >> 8) & 255,
                               (page_id >> 16) & 255, 0, 0, 0, 0, 0],
    "A": lambda page_id, aid: [aid & 255, (aid >> 8) & 255, (aid >> 16) & 255,
                               (aid >> 24) & 255, 0],
}

#: Payload offsets that are still unidentified (the reverse is not finished).
CURRENT_UNSOLVED = {
    "ua": (10, 11, 50, 51),            # UA-derived, pipeline not identified
    "time": (12, 15, 32, 35, 37, 39, 42, 43, 44, 68),
    "env_bits": (18, 21),
    "fingerprint": (76, 77, 78, 79, 81, 82, 83, 84, 100, 101, 102, 103, 112, 113, 115),
    "checksum": (133,),
    "prefix4": None,                    # the 4-byte nonce is not in the payload
}


def build_current_payload(params, body, user_agent, *, page_id=PAGE_ID, aid=AID,
                          template=None, strict=True):
    """Assemble the live payload buffer.

    ``template`` is a 134-byte capture of the constant slots (obtainable with
    ``refs/hook_dump.js``; see EVIDENCE.md section 5).  Slots listed in
    ``CURRENT_UNSOLVED`` that the template cannot supply raise
    :class:`IncompleteReverseError` when ``strict`` is true.
    """
    missing = []
    if template is None or len(template) != CURRENT_PAYLOAD_LEN:
        missing.append("payload template (constant slots) not supplied")
    if missing:
        raise IncompleteReverseError(
            "cannot assemble the live a_bogus payload yet: " + "; ".join(missing)
            + ". Unidentified offsets: " + ", ".join(
                "%s=%s" % (k, v) for k, v in CURRENT_UNSOLVED.items() if v))

    q = _hash2(params, SALT)
    b = _hash2(body, SALT)
    sel = CURRENT_SOURCE_SEL
    out = bytearray(template)
    for idx, kind, sel_idx, mask, const in CURRENT_SLOT_RULES:
        if kind == "Q":
            src = q[sel_idx]
        elif kind == "B":
            src = b[sel_idx]
        elif kind == "CK":
            raise IncompleteReverseError(
                "payload[133] is a checksum whose exact formula is not solved")
        else:
            src = sel[kind](page_id, aid)[sel_idx]
        contribution = (src & mask) ^ const
        out[idx] = out[idx] ^ contribution if kind == "P" and idx == 59 else contribution
    return bytes(out)


def current_encode(payload, prefix4):
    """Exact live final-encoding step (verified bit-for-bit against bdms.js).

    Returns the ``a_bogus`` value for a 134-byte payload and a 4-byte nonce.
    """
    if len(payload) != CURRENT_PAYLOAD_LEN:
        raise ValueError("payload must be %d bytes, got %d"
                         % (CURRENT_PAYLOAD_LEN, len(payload)))
    if len(prefix4) != 4:
        raise ValueError("prefix must be 4 bytes")
    masked = bytes(a ^ b for a, b in zip(payload, KEYSTREAM))
    return b64_custom(bytes(prefix4) + masked, OUT_ALPHABET)


# ==========================================================================
# 5. public entry points
# ==========================================================================

def a_bogus(params, body, user_agent, **kwargs):
    """Return the ``a_bogus`` query value.

    ``params``      URL query string without a leading ``?``, insertion order kept
    ``body``        raw request body (``""`` for GET)
    ``user_agent``  exact UA sent with the request

    .. warning::
       This currently returns the **legacy** algorithm's value.  The live
       generator (bdms 1.0.1.19-fix.01) uses a different payload permutation and
       a different cipher; see the module docstring and EVIDENCE.md section 4.
       For the UWP client this is not blocking: the douyin web API does not
       validate ``a_bogus`` as of 2026-10-07 (EVIDENCE.md section 6).
    """
    return a_bogus_legacy(params, body, user_agent, **kwargs)


def sign_query(params, body, user_agent, **kwargs):
    """Append ``&a_bogus=`` to ``params``."""
    return "%s&a_bogus=%s" % (params, a_bogus(params, body, user_agent, **kwargs))


# ==========================================================================
# 6. CLI
# ==========================================================================

def _selftest():
    ok = True
    checks = []

    def expect(name, got, want):
        good = got == want
        checks.append((name, good, got, want))
        return good

    expect("sm3('abc')", sm3(b"abc").hex(),
           "66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0")
    expect("sm3('abcd'*16)", sm3(b"abcd" * 16).hex(),
           "debe9ff92275b8a138604889c18e5a4d6fdb70e5387e5765293dcba39c0c5732")
    probe = [i & 0xFF for i in range(0, 1500, 7)]
    expect("perm roundtrip", list(perm_uncipher(perm_cipher(probe))), probe)
    raw = bytes(range(0, 60))
    expect("b64 roundtrip", b64_custom_decode(b64_custom(raw, OUT_ALPHABET), OUT_ALPHABET), raw)
    expect("keystream len", len(KEYSTREAM), CURRENT_PAYLOAD_LEN)

    for name, good, got, want in checks:
        if not good:
            ok = False
            print("FAIL %-20s got=%r want=%r" % (name, got, want))
        else:
            print("OK   %-20s" % name)
    return 0 if ok else 1


def _main(argv):
    if len(argv) > 1 and argv[1] == "--selftest":
        return _selftest()
    if len(argv) > 1 and argv[1] == "--sign":
        params = argv[2]
        body = argv[3] if len(argv) > 3 else ""
        ua = argv[4] if len(argv) > 4 else DEFAULT_UA
        sys.stdout.write(a_bogus(params, body, ua))
        return 0
    if len(argv) > 1 and argv[1] == "--state":
        print("live frame: a_bogus = b64_custom(prefix4 + (payload ^ KEYSTREAM), Z[255])")
        print("payload len: %d" % CURRENT_PAYLOAD_LEN)
        print("solved slot rules: %d" % len(CURRENT_SLOT_RULES))
        for k, v in CURRENT_UNSOLVED.items():
            print("  UNSOLVED %-12s %s" % (k, v))
        return 0
    return _selftest()


if __name__ == "__main__":
    raise SystemExit(_main(sys.argv))
