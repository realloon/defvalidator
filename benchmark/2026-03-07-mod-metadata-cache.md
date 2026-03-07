# Mod metadata cache benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: `dotnet src/DefValidator.Cli/bin/Release/net10.0/defvalidator.dll`
- Change: cache target/direct-dependency assembly metadata the same way we already cache Core metadata
- Note: benchmark used a temporary `HOME` inside the sandbox so cache writes could succeed without touching the real user home directory

## Cold
- `load_metadata=4605.6ms`
- `load_metadata.load_core_types=4034.0ms`
- `load_metadata.load_mod_types=523.9ms`
- `load_metadata.read_core_types_cache=0.5ms`
- `load_metadata.read_mod_types_cache=0.0ms`
- `load_metadata.write_core_types_cache=160.0ms`
- `load_metadata.write_mod_types_cache=12.1ms`
- `build_xml=857.1ms`
- `semantic_validate=189.8ms`
- `total=5684.7ms`

## Warm
- `load_metadata=124.2ms`
- `load_metadata.load_core_types=83.7ms`
- `load_metadata.load_mod_types=21.8ms`
- `load_metadata.read_core_types_cache=78.9ms`
- `load_metadata.read_mod_types_cache=21.5ms`
- `build_xml=441.1ms`
- `semantic_validate=200.5ms`
- `total=788.4ms`

## Conclusion
- Mod metadata cache is working: `read_mod_types_cache=21.5ms` on the warm run.
- Warm `load_metadata` dropped to about `124ms`.
- Warm end-to-end total dropped under `800ms` for this mod.
- After this change, the biggest remaining warm costs are `build_xml` and `semantic_validate`, not metadata loading.
