# Core XML cache benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: ~/.local/bin/defvalidator
- Cache: ~/Library/Caches/DefValidator/core-xml-*.xml
- Reference warm runs before this change: 2.13s-2.23s
- Metadata cache: warm

## After
cold run: 3.64s
warm run 1: 2.06s
warm run 2: 2.05s
warm run 3: 2.09s

## Verification
- exit codes: 0, 0, 0, 0
- stdout bytes:        0,        0,        0,        0

## Cache files
-rw-r--r--@ 1 lizhen  staff   5.9M Mar  7 20:51 /Users/lizhen/Library/Caches/DefValidator/core-xml-ec389621f465da5a039567891a6df4485a3b3d32a949a88ac770d6cd887db6a6.xml

## Conclusion
- Warm runs improve slightly from about `2.13s`-`2.23s` to about `2.05s`-`2.09s`.
- Cold run is still acceptable at `3.64s` with metadata cache already warm.
- The cached Core XML aggregate is much smaller than the metadata cache and cheap to reuse.
