# XML aggregation move-nodes benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: `dotnet src/DefValidator.Cli/bin/Release/net10.0/defvalidator.dll`
- Change: stop deep-cloning XML nodes during aggregate building, sanitize the core cache in place, and read cached XML without `PreserveWhitespace`
- Note: benchmark used a temporary `HOME` inside the sandbox so cache writes could succeed without touching the real user home directory

## Before
- Reference: `benchmark/2026-03-07-mod-metadata-cache.md`
- Warm `build_xml=441.1ms`
- Warm `build_xml.load_core_cache=273.4ms`
- Warm `build_xml.read_core_cache=94.2ms`
- Warm `build_xml.resolve_inheritance=166.1ms`
- Warm `total=788.4ms`

## After
cold:
- `build_xml=551.6ms`
- `build_xml.load_core_cache=398.3ms`
- `build_xml.build_core_aggregate=207.9ms`
- `build_xml.resolve_core_inheritance=133.8ms`
- `build_xml.sanitize_core_cache=14.6ms`
- `total=5195.3ms`

warm:
- `build_xml=207.3ms`
- `build_xml.load_core_cache=80.1ms`
- `build_xml.read_core_cache=70.7ms`
- `build_xml.resolve_inheritance=126.0ms`
- `total=537.7ms`

## Conclusion
- Warm `build_xml` dropped from about `441ms` to about `207ms`.
- Warm end-to-end total dropped from about `788ms` to about `538ms`.
- The saved time mainly comes from eliminating repeated deep XML cloning during aggregation.
- After this change, warm `semantic_validate` is now roughly the same size as warm `build_xml`.
