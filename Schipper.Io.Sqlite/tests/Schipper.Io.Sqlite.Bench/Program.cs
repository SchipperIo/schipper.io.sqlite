using Schipper.Io.Sqlite.Bench;

// Two modes over the same source-generated repositories.
//
//   bench   single-threaded throughput. Answers "how fast is one caller", per operation.
//   chaos   concurrent mixed workload against a pre-populated database. Answers the question that
//           actually decides whether SQLite survives production: what happens when several workers
//           write at once.
//
//   Schipper.Io.Sqlite.Bench                  bench, default scales
//   Schipper.Io.Sqlite.Bench bench 50000      bench at one scale
//   Schipper.Io.Sqlite.Bench chaos            chaos, 8 workers, 15s
//   Schipper.Io.Sqlite.Bench chaos 16 30      chaos, 16 workers, 30s
//   Schipper.Io.Sqlite.Bench audit            verify the audit + concurrency rules
//
// Or by environment, for CI and container runs where arguments are awkward:
//   SCHIPPER_PERF_MODE=chaos|bench
//   SCHIPPER_CHAOS_WORKERS, SCHIPPER_CHAOS_SECONDS, SCHIPPER_CHAOS_SEED
//   SCHIPPER_BENCH_ROWS

var mode = (Arg(0) ?? Env("SCHIPPER_PERF_MODE") ?? "bench").ToLowerInvariant();

return mode switch
{
    "chaos" => await Chaos.RunAsync(
        workers: Int(Arg(1) ?? Env("SCHIPPER_CHAOS_WORKERS"), Math.Max(4, Environment.ProcessorCount)),
        duration: TimeSpan.FromSeconds(Int(Arg(2) ?? Env("SCHIPPER_CHAOS_SECONDS"), 15)),
        seed: int.TryParse(Env("SCHIPPER_CHAOS_SEED"), out var parsed) ? parsed : null),

    "bench" => await Benchmark.RunAsync(Scales()),

    "audit" => await Audit.RunAsync(),

    _ => Unknown(mode),
};

// 1,000,000 is deliberately not in the default set. Throughput is flat from 1k upward, so the
// larger run measures SQLite's ceiling rather than anything about the generated code — it costs a
// minute and reports what 100k already did. Pass it explicitly if you want it.
int[] Scales()
{
    var explicitRows = Int(Arg(1) ?? Env("SCHIPPER_BENCH_ROWS"), 0);
    return explicitRows > 0 ? [explicitRows] : [1_000, 10_000, 100_000];
}

static string? Arg(int index)
{
    var args = Environment.GetCommandLineArgs();
    return args.Length > index + 1 ? args[index + 1] : null;
}

static string? Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

static int Int(string? text, int fallback) => int.TryParse(text, out var value) ? value : fallback;

static int Unknown(string mode)
{
    Console.Error.WriteLine($"Unknown mode '{mode}'. Expected 'bench', 'chaos' or 'audit'.");
    return 2;
}
