# -*- coding: utf-8 -*-
"""Douyin Web protocol layer for Project-Desultory-Signals.

This package is deliberately **pure standard library** and **transport-free**:

* It never opens a socket and never imports ``ssl``/``socket``. TLS and all
  network I/O belong to the native (C#) transport, because the embedded
  CPython 3.15 runtime on Windows 10 Mobile has no ``_ssl`` module.
* It never touches the filesystem. Cookie jar, virtual browser identity and
  session blobs are passed in and handed back as JSON so the host owns storage.
* Every operation is a pure function of its inputs plus an injected
  :class:`~douyin.clock.Clock`; nothing reads the wall clock or ``random``
  directly. That is what makes signing reproducible and unit-testable on a
  desktop interpreter (see ``tests/``).

Entry point for the host is :func:`douyin.protocol.handle`.
"""

__all__ = ["protocol"]
