# Installer coding rules

Applies to the WPF setup app (.NET Framework 4.8, C# 7.3). Use the shared
[formatting configuration](../.editorconfig). Check formatting with
`dotnet format whitespace installer-wpf --folder --verify-no-changes`; drop
`--verify-no-changes` to apply it. Folder mode is required because the project
is not SDK-style.

- Use PascalCase for types, methods, properties, constants, and non-private
  fields, and camelCase for locals, parameters, and private fields (no `_`
  prefix; use `this.` only to disambiguate). Name flag and enum values passed to
  native APIs with constants that keep the Win32 name, such as
  `COINIT_APARTMENTTHREADED`, instead of bare numbers. Shared Win32 error codes
  and the driver's mode values (`ModeOff`, `ModeSimple`, `ModeCompliant`) live
  in `Platform`; other API constants sit with the P/Invoke declarations that use
  them.
- Put one statement on each line and a blank line between members. Short
  property accessors and empty bodies may stay on one line. Keep lines within
  120 columns.
- Keep page state in `UiState` and the window in code-behind; do not add an MVVM
  framework. Touch WPF objects only on the UI thread, and marshal to it with
  `Dispatcher.BeginInvoke`. Install and uninstall work runs on the controller's
  STA worker through `OnWorker`; document which thread owns each piece of state.
- Use `async void` only for event handlers, and catch exceptions in them. Do not
  use `ConfigureAwait(false)` on paths that return to the UI. Catch exceptions
  where they can be reported to the user or mapped to an exit code, not deeper.
- Declare P/Invoke signatures at the end of the class that uses them, private
  unless shared. Set `CharSet = CharSet.Unicode` and the `W` entry point for
  string APIs and `SetLastError = true` when the API reports through
  `GetLastError`. Check every result, then read `Marshal.GetLastWin32Error()` or
  throw with `Marshal.ThrowExceptionForHR` right away. Release handles in
  `finally` blocks.
- Keep user-facing text short and specific about what changes on the device.
  Explain ordering, restart requirements, and driver-store behavior in comments
  rather than restating API calls.
- Run `build-wpf.cmd` after changing the installer or its embedded driver package.

These rules adapt Microsoft's [.NET C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)
and [Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)
to this codebase. Where the existing code and these sources differ, such as
private field naming, follow this file.
