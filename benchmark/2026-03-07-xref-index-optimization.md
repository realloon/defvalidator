# Xref index optimization profiling

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: ~/.local/bin/defvalidator
- Change: replace linear `_defs.Any(...)` reference matching with a `defName -> defs[]` index

## Before
- `load_metadata=412.2ms`
- `build_xml=331.7ms`
- `semantic_validate=1081.4ms`
- `semantic_validate.resolve_references=940.8ms`
- `semantic_validate.resolve_reference_match=932.7ms count=16179`
- `total=1854.1ms`

## After
run 1:
- `load_metadata=403.7ms`
- `build_xml=389.5ms`
- `semantic_validate=155.0ms`
- `semantic_validate.index_defs_by_name=1.4ms`
- `semantic_validate.resolve_references=10.0ms`
- `semantic_validate.resolve_reference_match=1.9ms count=16179`
- `total=973.3ms`

run 2:
- `load_metadata=437.3ms`
- `build_xml=361.5ms`
- `semantic_validate=154.0ms`
- `semantic_validate.index_defs_by_name=1.6ms`
- `semantic_validate.resolve_references=10.7ms`
- `semantic_validate.resolve_reference_match=1.9ms count=16179`
- `total=978.0ms`

## Notes
- Fine-grained semantic timings are nested and therefore not additive.
- The main hotspot was repeated linear scans over `_defs` during xref resolution.

## Conclusion
- `resolve_references` dropped from about `940ms` to about `10ms`.
- `semantic_validate` dropped from about `1081ms` to about `154ms`.
- End-to-end total dropped from about `1854ms` to about `973ms` on this mod.
