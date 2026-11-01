# -*- coding: utf-8 -*-
"""Encoding, hashing and 32-bit arithmetic helpers.

Kept in one place so the rest of the layer never depends on a specific
extension module being importable. On the embedded CPython 3.15 runtime
``hashlib`` is backed by the ``_md5``/``_sha1``/``_sha2``/``_sha3``/``_blake2``
builtins (there is no ``_hashlib``/OpenSSL), and ``binascii`` provides CRC32.
"""

import base64
import binascii
import hashlib
import hmac
import struct

MASK32 = 0xFFFFFFFF


# ---------------------------------------------------------------------------
# Text / bytes
# ---------------------------------------------------------------------------

def to_bytes(value, encoding="utf-8"):
    if isinstance(value, bytes):
        return value
    if isinstance(value, bytearray):
        return bytes(value)
    if isinstance(value, str):
        return value.encode(encoding)
    return str(value).encode(encoding)


def to_text(value, encoding="utf-8"):
    if isinstance(value, str):
        return value
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode(encoding, "replace")
    return str(value)


# ---------------------------------------------------------------------------
# Hashing
# ---------------------------------------------------------------------------

def md5_hex(data):
    return hashlib.md5(to_bytes(data)).hexdigest()


def md5_bytes(data):
    return hashlib.md5(to_bytes(data)).digest()


def sha256_hex(data):
    return hashlib.sha256(to_bytes(data)).hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(to_bytes(data)).digest()


def hmac_sha256(key, data):
    return hmac.new(to_bytes(key), to_bytes(data), hashlib.sha256).digest()


def hmac_sha256_hex(key, data):
    return binascii.hexlify(hmac_sha256(key, data)).decode("ascii")


def crc32(data):
    return binascii.crc32(to_bytes(data)) & MASK32


# ---------------------------------------------------------------------------
# Base64
# ---------------------------------------------------------------------------

def b64_encode(data):
    return base64.b64encode(to_bytes(data)).decode("ascii")


def b64_decode(text):
    return base64.b64decode(to_bytes(text))


def b64_encode_custom(data, alphabet, pad=False):
    """Standard base64 with a substituted alphabet.

    The web signer family emits several values through a shuffled alphabet;
    ``pad`` controls whether ``=`` padding is emitted, because the JS originals
    frequently do not pad and the server compares the literal string.
    """
    if len(alphabet) != 64:
        raise ValueError("alphabet must contain exactly 64 characters")
    encoded = base64.b64encode(to_bytes(data)).decode("ascii").rstrip("=")
    standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
    return encoded.translate(str.maketrans(standard, alphabet)) + ("=" if pad else "")


# ---------------------------------------------------------------------------
# URL escaping that matches JavaScript's encodeURIComponent
# ---------------------------------------------------------------------------

# encodeURIComponent leaves these unescaped: A-Z a-z 0-9 - _ . ! ~ * ' ( )
_JS_UNESCAPED = frozenset(
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()"
)


def js_encode_uri_component(value):
    """Reproduce JavaScript ``encodeURIComponent`` exactly.

    ``urllib.parse.quote`` differs in the "safe" set (it escapes ``!~*'()``),
    and the server compares the signed string byte for byte, so the difference
    is fatal to the signature.
    """
    text = to_text(value)
    out = []
    for ch in text:
        if ch in _JS_UNESCAPED:
            out.append(ch)
        else:
            for byte in ch.encode("utf-8"):
                out.append("%%%02X" % byte)
    return "".join(out)


# ---------------------------------------------------------------------------
# 32-bit arithmetic helpers used by the signature family
# ---------------------------------------------------------------------------

def u32(value):
    return value & MASK32


def rotr32(value, count):
    value = u32(value)
    count &= 31
    if count == 0:
        return value
    return u32((value >> count) | (value << (32 - count)))


def rotl32(value, count):
    value = u32(value)
    count &= 31
    if count == 0:
        return value
    return u32((value << count) | (value >> (32 - count)))


def pack_u32_le(value):
    return struct.pack("<I", u32(value))


def unpack_u32_le(data, offset=0):
    return struct.unpack_from("<I", data, offset)[0]


def rc4(key, data):
    """Classic RC4. Used both by the signer family and by legacy X-Bogus."""
    key = to_bytes(key)
    data = to_bytes(data)
    if not key:
        raise ValueError("rc4 key must not be empty")
    state = list(range(256))
    j = 0
    key_length = len(key)
    for i in range(256):
        j = (j + state[i] + key[i % key_length]) & 0xFF
        state[i], state[j] = state[j], state[i]
    out = bytearray(len(data))
    i = 0
    j = 0
    for index in range(len(data)):
        i = (i + 1) & 0xFF
        j = (j + state[i]) & 0xFF
        state[i], state[j] = state[j], state[i]
        out[index] = data[index] ^ state[(state[i] + state[j]) & 0xFF]
    return bytes(out)
