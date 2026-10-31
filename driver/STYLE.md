# Driver coding rules

Applies to the UMDF 2 filter and its report-processing code. Use the shared
[formatting configuration](../.clang-format). Check selected C++ files with
`clang-format --style=file --dry-run --Werror <file>`; use `-i` to format them.

- Use descriptive PascalCase names for functions and types, and camelCase for
  variables and parameters. Preserve Windows/WDF names and existing constant
  names; prefer typed `constexpr` values for new constants.
- Keep implementations in `.cpp` files and shared declarations in self-contained
  `.h` files. Inline functions and templates may live in headers, including the
  report-processing code. Keep helpers private when they are used in only one
  source file.
- Use WDF callback declarations and applicable SAL annotations to describe API
  contracts. Use Windows types at WDF boundaries and `NT_SUCCESS` for `NTSTATUS`;
  do not substitute Win32 error checks for NT status checks.
- Make request ownership explicit: forward, retain for completion, or complete
  exactly once. Check send failures and keep asynchronous buffers and contexts
  alive until the framework is finished with them.
- Validate report lengths, capacities, IDs, and offsets before accessing bytes.
  Keep report processing bounded and avoid allocations or blocking work in the
  read/completion path. Keep exceptions inside C++ code; never let them escape a
  WDF callback.
- Put device and reader state in the corresponding WDF contexts. Document which
  lock protects shared state and keep its scope small. Explain ownership,
  ordering, and firmware workarounds in comments rather than restating API calls.
- Keep report transformations independent of device I/O. Run `driver\analyze.cmd`
  and `driver\build.cmd` after changing report-processing or WDF integration code.

These rules adapt Microsoft's [Surface Team Driver Development Best Practices](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/surface-team-driver-development-best-practices).
This is a user-mode driver: apply kernel-only restrictions only when relevant to
the actual API and execution context.
