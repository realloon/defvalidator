# Inheritance clone reduction benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: `dotnet src/DefValidator.Cli/bin/Release/net10.0/defvalidator.dll`
- Change: reduce deep XML cloning during inheritance resolution by reusing source nodes where safe and moving child nodes into merged trees instead of cloning them again
- Note: benchmark used a temporary `HOME` inside the sandbox so cache writes could succeed without touching the real user home directory

## Before
- Reference: `benchmark/2026-03-07-xml-move-nodes.md`
- Warm `build_xml=207.3ms`
- Warm `build_xml.load_core_cache=80.1ms`
- Warm `build_xml.read_core_cache=70.7ms`
- Warm `build_xml.resolve_inheritance=126.0ms`
- Warm `semantic_validate=197.7ms`
- Warm `total=537.7ms`

## After
cold:
- `build_xml=422.7ms`
- `build_xml.load_core_cache=383.6ms`
- `build_xml.resolve_core_inheritance=105.1ms`
- `build_xml.resolve_inheritance=38.2ms`
- `semantic_validate=216.8ms`
- `total=5220.5ms`

warm:
- `build_xml=110.7ms`
- `build_xml.load_core_cache=85.8ms`
- `build_xml.read_core_cache=75.0ms`
- `build_xml.resolve_inheritance=23.4ms`
- `semantic_validate=165.7ms`
- `total=423.7ms`

## Conclusion
- Warm `build_xml` dropped from about `207ms` to about `111ms`.
- Warm `resolve_inheritance` dropped from about `126ms` to about `23ms`.
- Warm end-to-end total dropped from about `538ms` to about `424ms`.
- This change is worthwhile because it improves both the dedicated inheritance stage and overall runtime without changing the result for the clean target mod.
