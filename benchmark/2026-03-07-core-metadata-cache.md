# Core metadata cache benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: ~/.local/bin/defvalidator
- Cache: ~/Library/Caches/DefValidator/core-types-*.json
- Reference warm runs before this change: 3.92s-3.94s

## After
cold run: 4.57s
warm run 1: 2.23s
warm run 2: 2.16s
warm run 3: 2.13s

## Verification
- All runs exit `0`
- All runs produce `0` bytes on stdout

## Cache
- Narrowed cached game assemblies to `Assembly-CSharp*`, `UnityEngine*`, and `Unity.*`
- Cache file size: `15M`
- Cached type count: `97408`

## Conclusion
- Cold run stays acceptable because cache build no longer walks every `System.*` game DLL.
- Warm runs improve from about `3.93s` to about `2.13s`-`2.23s` on this mod.
