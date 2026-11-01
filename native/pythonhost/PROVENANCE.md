# Project-Desultory-Signals
#
# PROVENANCE
#
# `pyuwp-shim.h` and `pyuwp-stubs.c` are copied verbatim from the external
# AlterGate / PyUWP CPython 3.15 port so that this repository can link its own
# host driver against that runtime without modifying a tree it does not own.
#
# Source tree : H:\项目\UWP\Python UWP\port\
# Copied on   : 2026-10-07
# SHA-256     : pyuwp-shim.h  2EE233025FC0565A4FF171385AF381810E83243C3B0A3B36DF3477559E2584AD
#               pyuwp-stubs.c 38E8867B0809A7697C30883A170BBA3B6F94F7EDA4DA80F248D57BD9B5276236
#
# Why copies instead of references: the external tree is a separate repository
# with its own history. Editing it to add our ABI would put our project's
# requirements into someone else's source of truth, and a future pull there
# would silently change what we link against. Copying makes the dependency
# explicit and reviewable.
#
# What each file is:
#
#   pyuwp-shim.h    Force-included (/FI) back-fill of the Win32 declarations the
#                   Windows SDK hides behind WINAPI_PARTITION_DESKTOP when
#                   WINAPI_FAMILY=WINAPI_FAMILY_APP. It declares constants and
#                   types only; APIs that genuinely do not exist in an App
#                   Container still fail loudly at link time.
#
#   pyuwp-stubs.c   The handful of symbols CPython's Windows core references that
#                   an App Container cannot provide, plus real implementations
#                   where a UWP-legal equivalent exists (GetActiveProcessorCount
#                   via GetNativeSystemInfo).
#
# `desultory-driver.c` is our own fork of `pyuwp-driver.c`; the fork is documented
# at the top of that file and in `docs/ARCHITECTURE.md`.
#
# Refresh procedure: re-copy both files from the source tree and update the
# hashes above in the same commit, so a diff shows exactly what moved.
