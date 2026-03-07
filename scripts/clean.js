#!/usr/bin/env bun
import { rmSync } from "node:fs";
import { join } from "node:path";

const rootDir = join(import.meta.dir, "..");
const targets = ["bin", "obj", "TestResults"];
const roots = [join(rootDir, "src"), join(rootDir, "tests")];

for (const baseDir of roots) {
  for (const target of targets) {
    rmSync(join(baseDir, target), { force: true, recursive: true });
  }
}
