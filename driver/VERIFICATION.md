# Formatting and static analysis

Run these commands from the repository root. They build or inspect code without
installing a driver or changing device settings. The build prerequisites and WDK
setup are documented in `driver/build.cmd`.

## Formatting

The root `.clang-format` applies to the project-owned driver C++ sources. The existing configuration uses clang-format 23.1.3.
Check selected files without rewriting them:

```powershell
clang-format --style=file --dry-run --Werror driver/legomycursor.cpp driver/reportfix.h
```

Use `-i` instead of `--dry-run --Werror` to format selected files. Do not format
generated files, the SDK, or the WDK.

## MSVC, clang-tidy, and Cppcheck

```powershell
.\driver\check-reports.cmd
.\driver\build.cmd
.\driver\analyze.cmd
```

`check-reports.cmd` exercises the production report-processing boundary without
installing the driver, asserting on report bytes: Off pass-through, Simple tip
clears, Compliant last-position restores and its null-file fallback, packed
reports, short buffers, capacity clamping, and report IDs.

The driver build runs MSVC `/W4 /WX /analyze` and writes
`driver/out/legomycursor.sarif`.

The independent analyzer runner uses clang-tidy 23.1.4 and Cppcheck 2.22.0. Put
them on PATH or supply absolute paths with `-ClangTidy` and `-Cppcheck`. It also
recognizes local tools under `build/analyzers/clang-tidy/clang_tidy/data/bin/`
and `build/analyzers/cppcheck/PFiles/Cppcheck/`.

The runner checks the production driver and its included `reportfix.h` using
the compiler flags and include paths from `build-env.cmd`. Clear `CL` and `_CL_`
environment overrides before running it. Clang uses a generated filesystem
overlay to reconcile one enum declaration in the pinned UMDF headers; installed
headers remain untouched. Cppcheck reads project code extracted from MSVC's
preprocessed output and uses its Windows library model. It does not model WDF
ownership, callbacks, or locks.

Tool versions, compilation databases, logs, and reports are written under
`driver/out/analysis/`. Findings or tool failures produce a nonzero exit status.

## CodeQL

```powershell
codeql pack download codeql/cpp-queries@1.9.2 microsoft/windows-drivers@1.10.0 microsoft/cpp-queries@0.0.5
powershell -NoProfile -ExecutionPolicy Bypass -File driver/codeql.ps1
```

The existing tooling uses CodeQL CLI 2.27.3. Put it on PATH, supply
`-CodeQL C:\path\codeql.exe`, or install it at
`build/analyzers/codeql-cli/codeql/codeql.exe`.

The runner traces the production driver build (`build.cmd`) and analyzes that
database with the pinned C++ security-extended and Microsoft recommended driver
suites. It retains databases, logs, and SARIF reports under
`driver/out/codeql/<timestamp>/`. Findings remain visible and cause a nonzero
exit status. Analysis runs locally and does not upload source or results.

Analyzer executables, query caches, and generated reports are build dependencies
or output, not source files to commit.
