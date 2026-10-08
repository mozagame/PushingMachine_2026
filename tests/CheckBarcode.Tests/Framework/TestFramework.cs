using System.Diagnostics;
using System.Reflection;

namespace CheckBarcode.Tests.Framework;

/// <summary>Marks a test method. <see cref="Ids"/> lists the PLAN.md feature IDs covered (traceability).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public TestAttribute(params string[] ids) => Ids = ids;
    public string[] Ids { get; }
}

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message = "expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message ?? "values differ"}: expected <{expected}> but was <{actual}>");
    }

    public static void NotNull(object? value, string message = "expected non-null")
    {
        if (value is null) throw new AssertionException(message);
    }

    public static void Null(object? value, string message = "expected null")
    {
        if (value is not null) throw new AssertionException(message + $" (was {value})");
    }

    public static void Contains(string expected, string actual, string? message = null)
    {
        if (actual is null || !actual.Contains(expected, StringComparison.Ordinal))
            throw new AssertionException($"{message ?? "substring missing"}: <{expected}> not found in <{actual}>");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertionException($"expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertionException($"expected {typeof(T).Name} but nothing was thrown");
    }

    /// <summary>Polls a condition (for asynchronous device events).</summary>
    public static async Task Eventually(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new AssertionException("timeout: " + message);
    }
}

public static class TestRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var traceAt = Array.IndexOf(args, "--trace");
        var filter = args.Where((a, i) => !a.StartsWith("--") && (traceAt < 0 || i != traceAt + 1)).FirstOrDefault();
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
                .Select(m => (Type: t, Method: m, Attr: m.GetCustomAttribute<TestAttribute>()!)))
            .Where(x => filter is null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Name).ThenBy(x => x.Method.MetadataToken)
            .ToList();

        int passed = 0, failed = 0;
        var trace = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (type, method, attr) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            var sw = Stopwatch.StartNew();
            try
            {
                var instance = method.IsStatic ? null : Activator.CreateInstance(type);
                try
                {
                    var result = method.Invoke(instance, null);
                    if (result is Task task)
                    {
                        var done = await Task.WhenAny(task, Task.Delay(60_000));
                        if (done != task) throw new AssertionException("test timed out after 60 s");
                        await task;
                    }
                }
                finally
                {
                    if (instance is IAsyncDisposable ad) await ad.DisposeAsync();
                    else if (instance is IDisposable d) d.Dispose();
                }
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
                trace.Add($"| {string.Join(", ", attr.Ids)} | {name} | PASS |");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                failed++;
                Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                Console.WriteLine(Indent(inner.StackTrace));
                trace.Add($"| {string.Join(", ", attr.Ids)} | {name} | FAIL |");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {tests.Count} total in {total.Elapsed.TotalSeconds:0.0} s");
        var traceIndex = Array.IndexOf(args, "--trace");
        if (traceIndex >= 0 && traceIndex + 1 < args.Length)
        {
            File.WriteAllLines(args[traceIndex + 1], new[] { "| Feature IDs | Test | Result |", "|---|---|---|" }.Concat(trace));
        }
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }

    private static string Indent(string? s) => s is null ? "" : string.Join(Environment.NewLine, s.Split('\n').Take(6).Select(l => "        " + l.Trim()));
}
