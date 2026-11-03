# Changelog

All notable changes to this package are recorded here.

## 0.1.0 — First public candidate (2026-11-02)

- Select MIT with the copyright holder's explicit approval; add LICENSE, metadata and a standalone release-scope document. No fonts, art, generated Prefabs, local settings or internal development records are bundled.
- Document direct Editor API usage, a copyable read-only menu example, three optional integration samples and the separately deployed Bridge v0.3.0 candidate.
- Add native layout measurement and saved-composition diagnostics with node-level evidence and dependency-bound revision proposals. Revisions target a new Spec/new Prefab; existing Prefabs are not patched.
- Include Menu Flow and Audio Mixer integration samples; application callbacks, audio policy and persistence remain consumer-owned.
- Core regression baseline: 321/321 in Unity 2022.3.62f3c1. The prior r4 ZIP separately passed isolated installation, two README menu calls and three Sample imports/four compiled types. This licensing/documentation preparation does not change Package C# code.

The implementation entries below accumulated during local development before the first public Package. Their dated test counts are historical checkpoints, not additional current test runs.

- Add a constrained v2 native `Slider` node with explicit bounded range, initial value, whole-number mode, and stable Fill/Handle references; verify saved component state and empty persistent callbacks.
- Add Slider Spec validation and Prefab audit coverage for invalid ranges, out-of-range values, component references, and Raycast ownership.
- Add an opt-in v2 `confirmationDialog` contract with strict node-role validation, an inactive-by-default modal Prefab, full-screen pointer blocking, Confirm/Cancel UnityEvents, initial focus, close behavior, temporary topmost Canvas sorting and focus/sorting restoration; keep ordinary composition Specs and v1 behavior unchanged.
- Audit modal controller references and backdrop raycast/Button contracts; verify generated Prefab serialization and add validator, generation, audit, and runtime lifecycle regression coverage.
- Verify the import-audit increment in Sandbox with 302/302 full EditMode tests and 20/20 catalog/audit cases; exclude unrelated native assets without requesting manual conversion.
- Add a Kenney CC0 Grey/Gold confirmation-dialog Spec and runtime acceptance evidence; reuse sliced artwork at panel/button sizes without changing import settings.
- Add a read-only, bounded Sprite import-readiness audit with source dependency fingerprinting, exact Sprite identities, Simple/Sliced readiness, and explicit manual-review results.
- Preserve import settings, scene state, selection, and source dependency hashes during audit; no automatic import, slicing, border edits, or reimport.
- Add bounded read-only Sprite discovery with exact string local IDs, Spec references, importer/preview metadata and fingerprinted pagination; no imports or asset writes.
- Add 13 catalog EditMode cases; the catalog baseline passed 280/280 before import-audit additions. Companion Bridge local source now has six v2 tools, separate from the published 0.2.0 installer.

- Add explicit Multiple Sprite selection with paired name/string Int64 local file ID while preserving Single/native Spec behavior.
- Share read-only Sprite resolution between v2 preflight and Prefab audit; validate per-slice borders without name-only fallback.
- Include resolved per-node Sprite identity, Rect and Border in plans; persist distinct sub-Sprites from a shared sheet.

- Add a read-only v2 Spec-to-Prefab audit for stable node IDs, component shape, sprite identity/import mode/borders, Image type, colors, text style and Button/Raycast contracts.
- Add EditMode fixtures for valid, mutated, missing and unexpected composition Prefabs; verify the audit leaves asset bytes and the active scene unchanged.
- Keep Missing/ambiguous nodes, invalid Specs and Editor-not-ready states from reporting a passing result.

## Initial local 0.1.0 baseline - 2026-10-10

- Add read-only uGUI hierarchy inspection and desktop main-menu auditing.
- Add strict main-menu UI Spec validation and output-path checks.
- Add confirmed generation of a constrained desktop main-menu Prefab and scene instance.
- Add EditMode coverage for validation, auditing, generation, cleanup, Undo, and the end-to-end authoring workflow.
- Document local installation, dependencies, test setup, API boundaries, and the unselected redistribution license.
- Correct v2 Prepare summaries to enumerate native Slider nodes, their initial/range and Fill/Handle readback scope, plus the absence of application callbacks and settings persistence.
