# Changelog

## Unreleased — 0.1.0

Project scaffolding and the build-order step 1 API probe. Not yet a usable mod.

- **Read-only recipe probe** (`.tallybook <item code>`): finds grid recipes producing a
  matching item and prints ingredients with live carried-inventory counts and satisfied /
  partial / none status, plus non-consumed tool rows. `.tallybook off` stops watching. This
  is spec §10 step 1 — it exists to validate registry access and inventory events against the
  real 1.22 API before anything is built on top of them.
- Inventory counting is driven by `IInventory.SlotModified`, not polling, and reports only
  values that actually changed.
- Design spec finalised (`tallybook-mod-spec.md`).
- Client-only mod skeleton: `modinfo.json` (`"side": "Client"`, requires game 1.22.0) and a
  `ModSystem` gated to `EnumAppSide.Client`.
- Compat regression harness (`tools/compat-test.ps1`): headless dedicated-server boots for
  every mod combination, with Pin Matrix as the companion mod. Fails on server-log errors or
  warnings, wrong mod count/load order, a missing assembly-load marker, or any loss of
  server-side silence.
- Game-version sweep (`tools/version-sweep.ps1`): builds one artifact and runs the full
  compat matrix against real dedicated servers for 1.22.0 through 1.22.6. Server packages are
  extracted with verification against the archive's entry count and a completion stamp, and
  setup problems are reported as `SETUP` rather than being misattributed as a mod failure.
