# Lazy dependency discovery benchmark

- Date: 2026-03-07
- Target: /Users/lizhen/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/BottledAbilities
- Binary: ~/.local/bin/defvalidator
- Scenario: validate target mod with auto-detected game directory

## Before
run 1: 4.11s
run 2: 3.97s
run 3: 3.96s

## After
run 1: 5.01s
run 2: 3.94s
run 3: 3.92s

## Conclusion
- Warm runs stay roughly the same: before `3.96s`-`3.97s`, after `3.92s`-`3.94s`.
- The change reduces eager workshop/mod discovery work, but BottledAbilities only has a small direct dependency set, so the win is marginal here.
