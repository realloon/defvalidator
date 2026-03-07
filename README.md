# DefValidator

A small CLI for validating RimWorld Def XML for a single target mod.

`DefValidator` aims to be close to game loading without executing game or mod code. It is intentionally narrower than a full runtime simulation.

## What it does

Given a target mod, `defvalidator` builds a lightweight validation context from:

- RimWorld `Core`
- the target mod
- the target mod's direct dependencies

It then validates:

- XML parsing under `Defs/`
- `MayRequire` / `MayRequireAnyOf`
- XML inheritance
- Def class and field metadata against reflected assemblies
- `defName` rules
- Def cross-references
- target-mod diagnostics only

## What it does not do

To keep the tool simple and predictable, it currently does **not**:

- execute RimWorld or mod code
- call `ConfigErrors()`
- apply XML patches
- replicate full in-game load behavior
- batch-scan many mods at once

## Scope

This project intentionally optimizes for:

- one target mod at a time
- static validation only
- low complexity
- fast repeat runs

## Usage

Basic usage:

```bash
defvalidator <mod-path>
```

Explicit game directory:

```bash
defvalidator <mod-path> --game-dir <path>
```

If `--game-dir` is omitted, `defvalidator` tries the default Steam install path for the current user.

## Output

Diagnostics are printed as plain text, one per line.

Example:

```text
/path/to/file.xml:12:4: error TYPE001: Unknown Def class 'ThingDef' [my.mod/ThingDef/MyDef]
```

Profiling can be enabled with:

```bash
DEFVALIDATOR_PROFILE=1 defvalidator <mod-path>
```

Profile lines are written to standard error.

## Exit codes

- `0`: no validation errors
- `1`: validation errors found
- `2`: CLI or environment failure

## Install

Download the single-file binary for your platform from the project's GitHub Releases page.

After downloading it:

- put `defvalidator` somewhere on your `PATH`, such as `~/.local/bin`
- make it executable if needed

Example:

```bash
chmod +x defvalidator
mv defvalidator ~/.local/bin/defvalidator
```

## Cache

`defvalidator` stores reusable caches in the current user's cache directory.

Default locations:

- macOS: `~/Library/Caches/DefValidator`
- Linux: `~/.cache/defvalidator`
- Windows: `%LocalAppData%\DefValidator`

The cache is safe to delete manually. `defvalidator` will rebuild it on the next run.

## Development notes

Useful commands:

```bash
dotnet build

dotnet test -m:1 --no-restore
```

The `-m:1` avoids MSBuild node issues in some restricted environments.

## Status

`DefValidator` is already useful as a focused terminal validator, but it is still intentionally conservative in scope.

Future work, if needed, should continue to prefer:

- simple behavior
- explicit boundaries
- measurable wins
- minimal moving parts
