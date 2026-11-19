# Razor runtime-async JIT regression probe

Standalone .NET 11 console app, with no external packages or application code. The Razor SDK compiles a synthetic MVC view containing 320 rows with built-in `label`, `input`, and validation tag helpers. The probe checks that the generated `ExecuteAsync` actually has `MethodImplAttributes.Async`, then compiles it in a fresh child process using `RuntimeHelpers.PrepareMethod`. It does not execute the view or start an HTTP server.

The SDK compiles `Views/Synthetic.cshtml` directly. Its 320 rows are written out so Razor emits separate tag helper calls rather than a loop. `Views/Control.cshtml` uses the same markup with 16 rows. There is no custom build task.

## Run

From this directory, using the .NET 11 SDK and ASP.NET Core runtime you want to test:

```powershell
dotnet --version
dotnet run -c Debug
```

To compare installations, invoke each installation's `dotnet` explicitly, e.g. `& 'C:\dotnet-under-test\dotnet.exe' run -c Debug`. The probe prints the actual .NET and ASP.NET Core versions selected by the host. Include both lines and the final result when reporting an issue.

| Result | Exit code | Meaning |
| --- | --- | --- |
| `OK` | 0 | The JIT finished within the probe's budgets. |
| `FAIL` | 1 | The worker crashed or was stopped after exceeding 15 seconds or 512 MiB of sampled private memory. |
| `ERROR` | 2 | The probe could not initialize; this is not a regression result. |

The monitor samples private memory roughly every 10 ms; the threshold is not a hard OS limit. `OK` applies to this fixture and budget, not to all Razor views. For the 16-row sanity check with `runtime-async` still enabled, run `dotnet run -c Debug -- --control`. Use the default 320 rows for version comparisons.

### Observed on Windows x64, .NET and ASP.NET Core 11.0.0, SDK 11.0.100

```text
Budget: 15s / 512 MiB private memory
runtime-async=on, IL=303256 bytes, IL locals=961
FAIL: memory budget exceeded (1.00s, sampled peak 522.9 MiB)
```

The failure occurs before any Razor code executes. Potentially related: [dotnet/runtime#137878](https://github.com/dotnet/runtime/issues/137878).