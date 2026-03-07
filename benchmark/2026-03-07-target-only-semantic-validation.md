# Target-only semantic validation benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: `dotnet src/DefValidator.Cli/bin/Release/net10.0/defvalidator.dll`
- Change: only run full semantic/type validation on the target mod's resolved defs; keep Core and direct dependencies only for def indexing and cross-reference resolution
- Motivation: the tool already filters diagnostics to the target mod, so fully walking every Core/dependency object tree was mostly wasted work
- Note: benchmark used a temporary `HOME` inside the sandbox so cache writes could succeed without touching the real user home directory

## Before
- Reference: `benchmark/2026-03-07-inheritance-clone-reduction.md`
- Warm `load_metadata=124.5ms`
- Warm `build_xml=110.7ms`
- Warm `semantic_validate=165.7ms`
- Warm `semantic_validate.validate_root_defs=152.5ms`
- Warm `total=423.7ms`

## After
cold:
- `load_metadata=4837.0ms`
- `build_xml=426.9ms`
- `semantic_validate=37.5ms`
- `semantic_validate.validate_root_defs=16.4ms`
- `total=5330.4ms`

warm:
- `load_metadata=141.0ms`
- `build_xml=112.8ms`
- `semantic_validate=15.6ms`
- `semantic_validate.validate_root_defs=10.4ms`
- `total=293.6ms`

## Conclusion
- Warm `semantic_validate` dropped from about `166ms` to about `16ms`.
- Warm end-to-end total dropped from about `424ms` to about `294ms`.
- This is a large win because the validator no longer re-validates thousands of Core objects when only target-mod diagnostics are surfaced.
