# MemoryPack core-types cache benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: ~/.local/bin/defvalidator
- Change: replace `core-types` JSON cache with `MemoryPack`
- Cache: ~/Library/Caches/DefValidator/core-types-*.mpk
- Reference profiled warm run before this change:
  - `load_metadata=533.8ms`
  - `build_xml=371.1ms`
  - `semantic_validate=1035.3ms`
  - `total=1964.3ms`

## After
cold run:
- `build_context=26.5ms`
- `load_metadata=1595.2ms`
- `build_xml=408.6ms`
- `semantic_validate=1143.7ms`
- `total=3174.9ms`

warm run 1:
- `build_context=21.4ms`
- `load_metadata=421.3ms`
- `build_xml=374.4ms`
- `semantic_validate=1026.1ms`
- `total=1844.0ms`

warm run 2:
- `build_context=24.2ms`
- `load_metadata=464.8ms`
- `build_xml=392.2ms`
- `semantic_validate=1192.6ms`
- `total=2074.6ms`

warm run 3:
- `build_context=33.7ms`
- `load_metadata=476.8ms`
- `build_xml=382.9ms`
- `semantic_validate=1040.6ms`
- `total=1934.8ms`

## Cache
- Cache file size: `11M`
- Cache format: `MemoryPack`

## Conclusion
- `load_metadata` improves by roughly `60ms`-`110ms` on warm runs.
- End-to-end total is only slightly better on average because `semantic_validate` is still the dominant cost and has noticeable run-to-run variance.
- For this project, `MemoryPack` is a measurable but modest optimization, not a breakthrough.
