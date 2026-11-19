using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Razor.Hosting;

const int memoryBudgetMiB = 512;
const int timeBudgetSeconds = 15;
const long bytesPerMiB = 1024 * 1024;

var assembly = Assembly.GetExecutingAssembly();
if (args is ["--worker", "Synthetic" or "Control"])
{
    var viewPath = $"/Views/{args[1]}.cshtml";
    var method = assembly.GetCustomAttributes<RazorCompiledItemAttribute>()
        .Single(item => item.Identifier == viewPath)
        .Type.GetMethod("ExecuteAsync")!;
    if ((method.MethodImplementationFlags & MethodImplAttributes.Async) == 0)
    {
        Console.Error.WriteLine("ERROR: Razor ExecuteAsync was not compiled with runtime async.");
        return 2;
    }

    var body = method.GetMethodBody()!;
    Console.WriteLine($"runtime-async=on, IL={body.GetILAsByteArray()!.Length} bytes, IL locals={body.LocalVariables.Count}");
    Console.WriteLine("READY");
    if (Console.ReadLine() != "GO") return 2;

    var jitTime = Stopwatch.StartNew();
    RuntimeHelpers.PrepareMethod(method.MethodHandle);
    Console.WriteLine($"JIT completed in {jitTime.Elapsed.TotalSeconds:F2}s");
    return 0;
}

if (args.Length != 0 && args is not ["--control"])
{
    Console.Error.WriteLine("Usage: dotnet run -c Debug [-- --control]");
    return 2;
}

var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var runtimeVersion = Path.GetFileName(runtimeDirectory);
var aspnetVersion = typeof(PartialTagHelper).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
Console.WriteLine($"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}); .NET {runtimeVersion}; ASP.NET Core {aspnetVersion}");
var viewName = args.Length == 0 ? "Synthetic" : "Control";
Console.WriteLine($"View: {viewName}.cshtml");
Console.WriteLine($"Budget: {timeBudgetSeconds}s / {memoryBudgetMiB} MiB private memory");

var start = new ProcessStartInfo(Environment.ProcessPath!)
{
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true
};
foreach (var arg in new[] { "--fx-version", runtimeVersion, assembly.Location, "--worker", viewName })
    start.ArgumentList.Add(arg);

using var worker = Process.Start(start)!;
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    var ready = false;
    while (await worker.StandardOutput.ReadLineAsync(startupTimeout.Token) is { } line)
    {
        if (line == "READY")
        {
            ready = true;
            break;
        }
        Console.WriteLine(line);
    }

    if (!ready || worker.HasExited)
    {
        Console.Error.WriteLine("ERROR: worker did not initialize.");
        return 2;
    }

    var remainingOutput = worker.StandardOutput.ReadToEndAsync();
    var stopwatch = Stopwatch.StartNew();
    worker.StandardInput.WriteLine("GO");
    worker.StandardInput.Flush();

    long peak = 0;
    string? failure = null;
    while (!worker.WaitForExit(10))
    {
        worker.Refresh();
        if (worker.HasExited) break;
        long memory;
        try { memory = worker.PrivateMemorySize64; }
        catch (InvalidOperationException) when (worker.HasExited) { break; }
        peak = Math.Max(peak, memory);
        if (peak >= memoryBudgetMiB * bytesPerMiB || stopwatch.Elapsed.TotalSeconds >= timeBudgetSeconds)
        {
            failure = peak >= memoryBudgetMiB * bytesPerMiB ? "memory budget exceeded" : "time budget exceeded";
            break;
        }
    }

    var elapsed = stopwatch.Elapsed.TotalSeconds;
    if (failure != null) StopWorker();
    await worker.WaitForExitAsync();
    Console.Write(await remainingOutput);
    if (failure == null && worker.ExitCode != 0) failure = $"worker exited with code {worker.ExitCode}";
    Console.WriteLine($"{(failure == null ? "OK" : $"FAIL: {failure}")} ({elapsed:F2}s, sampled peak {peak / (double)bytesPerMiB:F1} MiB)");
    return failure == null ? 0 : 1;
}
catch (OperationCanceledException) when (startupTimeout.IsCancellationRequested)
{
    Console.Error.WriteLine("ERROR: worker startup timed out.");
    return 2;
}
finally
{
    StopWorker();
}

void StopWorker()
{
    try { if (!worker.HasExited) worker.Kill(); }
    catch (InvalidOperationException) when (worker.HasExited) { }
}

namespace RazorRuntimeAsyncRepro
{
    public sealed class FormModel
    {
        public string Value { get; set; } = "";
    }
}