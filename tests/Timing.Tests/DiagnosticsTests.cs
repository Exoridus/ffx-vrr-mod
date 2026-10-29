namespace Fahrenheit.Mods.Vrr;

using System.Text.Json;
using Xunit;

public unsafe sealed partial class VrrModule
{
    private void diagnostic_event(string kind, object? data = null) { }
}

public sealed class DiagnosticsTests
{
    [Fact]
    public void Unarmed_events_allow_only_explicit_startup_metadata()
    {
        Assert.True(DiagnosticSession.AllowsEvent(false, "timing_hook"));
        Assert.True(DiagnosticSession.AllowsEvent(false, "native_render_validation"));
        foreach (string kind in new[] { "frame", "phase", "texture_sample", "probe_hook", "probe_binding", "initialization_complete", "timing_hook_suffix", "Timing_hook", "" })
            Assert.False(DiagnosticSession.AllowsEvent(false, kind));
    }

    [Fact]
    public void Armed_events_keep_the_existing_unrestricted_kind_policy()
    {
        foreach (string kind in new[] { "timing_hook", "native_render_validation", "frame", "texture_sample", "probe_binding" })
            Assert.True(DiagnosticSession.AllowsEvent(true, kind));
    }


    [Fact]
    public void Session_is_ordered_has_metadata_and_closes_without_loss()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var session = new DiagnosticSession(path, 100_000, 100, new { build = "fixture" }))
                for (int i = 0; i < 10; i++) Assert.True(session.TryWrite("phase", i, new { value = i }));
            var lines = File.ReadAllLines(path).Select(s => JsonDocument.Parse(s)).ToArray();
            try
            {
                Assert.Equal(12, lines.Length);
                Assert.Equal("session_start", lines[0].RootElement.GetProperty("kind").GetString());
                Assert.Equal("session_end", lines[^1].RootElement.GetProperty("kind").GetString());
                Assert.Equal(0, lines[^1].RootElement.GetProperty("dropped").GetInt64());
                Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), lines[..^1].Select(j => j.RootElement.GetProperty("sequence").GetInt64()));
                Assert.True(lines[0].RootElement.GetProperty("data").GetProperty("stopwatch_frequency").GetInt64() > 0);
                Assert.True(lines[0].RootElement.GetProperty("data").GetProperty("stopwatch_origin").GetInt64() > 0);
            }
            finally { foreach (var line in lines) line.Dispose(); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Budget_remains_bounded_and_reports_loss_in_valid_terminal_record()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var session = new DiagnosticSession(path, 4096, 8, new { }))
                for (int i = 0; i < 1000; i++) session.TryWrite("phase", i, new { text = new string('x', 512) });
            Assert.True(new FileInfo(path).Length <= 4096);
            using var end = JsonDocument.Parse(File.ReadLines(path).Last());
            Assert.Equal("session_end", end.RootElement.GetProperty("kind").GetString());
            Assert.True(end.RootElement.GetProperty("dropped").GetInt64() > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Concurrent_producers_and_dispose_do_not_throw_or_reorder_accepted_events()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var session = new DiagnosticSession(path, 2_000_000, 64, new { });
            Parallel.For(0, 500, i => { session.TryWrite("phase", i); if (i == 250) session.Dispose(); });
            session.Dispose();
            Assert.False(session.TryWrite("late", 501));
            long previous = 0;
            foreach (string line in File.ReadLines(path))
            {
                using var json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("sequence", out var sequence)) continue;
                Assert.True(sequence.GetInt64() > previous);
                previous = sequence.GetInt64();
            }
        }
        finally { File.Delete(path); }
    }
}
