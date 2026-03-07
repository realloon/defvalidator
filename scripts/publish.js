#!/usr/bin/env bun
import { existsSync } from "node:fs";
import { join } from "node:path";

const rootDir = join(import.meta.dir, "..");
const configuration = process.env.CONFIGURATION || "Release";
const targetFramework = "net10.0";
const project = join(rootDir, "src", "DefValidator.Cli", "DefValidator.Cli.csproj");

const args = process.argv.slice(2);
if (args.includes("-h") || args.includes("--help")) {
  printHelp();
  process.exit(0);
}

const rid = args[0] || detectRid();
const executableName = process.platform === "win32" ? "defvalidator.exe" : "defvalidator";
const publishDir = join(rootDir, "src", "DefValidator.Cli", "bin", configuration, targetFramework, rid, "publish");

const command = [
  "dotnet",
  "publish",
  project,
  "-c",
  configuration,
  "-r",
  rid,
  "--self-contained",
  "true",
  "-p:PublishSingleFile=true",
  "-p:PublishTrimmed=false",
];

console.log(`Publishing ${rid} (${configuration})...`);
const result = Bun.spawnSync(command, {
  cwd: rootDir,
  stdout: "inherit",
  stderr: "inherit",
  env: {
    ...process.env,
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  },
});

if (result.exitCode !== 0) {
  process.exit(result.exitCode);
}

const executablePath = join(publishDir, executableName);
if (!existsSync(executablePath)) {
  console.error(`Publish completed, but executable was not found: ${executablePath}`);
  process.exit(1);
}

console.log("Published single-file executable:");
console.log(executablePath);

function detectRid() {
  const arch = process.arch;

  if (process.platform === "darwin") {
    return arch === "arm64" ? "osx-arm64" : "osx-x64";
  }

  if (process.platform === "win32") {
    return arch === "arm64" ? "win-arm64" : "win-x64";
  }

  if (process.platform === "linux") {
    return arch === "arm64" ? "linux-arm64" : "linux-x64";
  }

  throw new Error(`Unsupported platform: ${process.platform}/${process.arch}`);
}

function printHelp() {
  console.log(`Usage: bun scripts/publish.js [rid]\n\nPublishes the defvalidator single-file executable.\n\nExamples:\n  bun scripts/publish.js\n  bun scripts/publish.js osx-arm64\n  CONFIGURATION=Debug bun scripts/publish.js win-x64`);
}
