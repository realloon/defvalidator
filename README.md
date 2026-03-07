# defvalidator

`defvalidator` is a small command-line tool for validating RimWorld Def XML for a single mod.

## Install

Download the single-file binary for your platform from the project's GitHub Releases page.

Then put `defvalidator` somewhere on your `PATH`, for example:

```bash
chmod +x defvalidator
mv defvalidator ~/.local/bin/defvalidator
```

## Usage

```bash
defvalidator <mod-path>
```

If needed, you can pass the RimWorld install directory explicitly:

```bash
defvalidator <mod-path> --game-dir <path>
```

If `--game-dir` is omitted, `defvalidator` tries the default Steam install path for the current user.

## Output

Diagnostics are printed as plain text, one per line.

## Exit codes

- `0`: no validation errors
- `1`: validation errors found
- `2`: CLI or environment failure

## Cache

`defvalidator` stores reusable caches in the current user's cache directory.

Default locations:

- macOS: `~/Library/Caches/defvalidator`
- Linux: `~/.cache/defvalidator`
- Windows: `%LocalAppData%\defvalidator`

The cache is safe to delete manually. `defvalidator` will rebuild it on the next run.
