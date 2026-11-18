using ApiAggregator.Engine.Logging;
using ApiAggregator.Engine.Logging.Live;
using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Tests;

/// <summary>The live log: a bounded in-memory buffer and the logger provider that fills it.</summary>
public class LiveLogTests
{
    private static LiveLogEntry Entry(string message = "m") =>
        new(0, DateTimeOffset.Now, "Information", "Test", 0, null, message);

    /// <summary>A logger factory wired to a buffer through the provider (the same way the hosts do it).</summary>
    private static (ILoggerFactory Factory, LiveLogBuffer Buffer) Create(LiveLogOptions? options = null)
    {
        var buffer = new LiveLogBuffer(100);
        var provider = new LiveLogProvider(buffer, options ?? new LiveLogOptions());
        // Everything reaches the provider; its own level rules decide what is kept.
        return (LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)), buffer);
    }

    [Fact]
    public void Buffer_keeps_only_the_newest_lines_when_it_is_full()
    {
        var buffer = new LiveLogBuffer(3);
        for (var i = 1; i <= 5; i++) buffer.Add(Entry($"line {i}"));

        Assert.Equal(new long[] { 3, 4, 5 }, buffer.Read(0).Select(e => e.Seq));
        Assert.Equal(5L, buffer.LatestSeq);
        Assert.Equal(3, buffer.Capacity);
    }

    [Fact]
    public void Read_after_a_sequence_number_returns_only_newer_lines_oldest_first()
    {
        var buffer = new LiveLogBuffer(10);
        for (var i = 1; i <= 4; i++) buffer.Add(Entry($"line {i}"));

        Assert.Equal(new long[] { 3, 4 }, buffer.Read(2).Select(e => e.Seq));
        Assert.Empty(buffer.Read(4));
    }

    [Fact]
    public void Read_returns_at_most_the_requested_number_of_lines()
    {
        var buffer = new LiveLogBuffer(10);
        for (var i = 1; i <= 4; i++) buffer.Add(Entry());

        Assert.Equal(new long[] { 1, 2 }, buffer.Read(0, 2).Select(e => e.Seq));
    }

    [Fact]
    public async Task Waiting_completes_as_soon_as_a_line_is_added()
    {
        var buffer = new LiveLogBuffer(10);
        var wait = buffer.WaitForNewAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(wait.IsCompleted);

        _ = Task.Run(async () => { await Task.Delay(50); buffer.Add(Entry()); });

        var winner = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(winner, wait)); // finished long before the 10 s timeout
        Assert.Equal(1, buffer.Read(0).Count);
    }

    [Fact]
    public async Task Waiting_returns_straight_away_when_newer_lines_already_exist()
    {
        var buffer = new LiveLogBuffer(10);
        buffer.Add(Entry());

        var wait = buffer.WaitForNewAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task Waiting_returns_after_the_timeout_when_nothing_arrives()
    {
        var buffer = new LiveLogBuffer(10);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await buffer.WaitForNewAsync(0, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.True(clock.ElapsedMilliseconds >= 50);
        Assert.Empty(buffer.Read(0));
    }

    [Fact]
    public void Provider_captures_scope_fields_and_the_event_id()
    {
        var (factory, buffer) = Create();
        var logger = factory.CreateLogger("ApiAggregator.Engine.Execution.CompositionExecutor");

        using (logger.BeginScope(new LogScope(("CorrelationId", "abc123"), ("RecipeId", "customer-overview"))))
        using (logger.BeginScope(new LogScope(("Step", "orders"), ("ServiceId", "service-b"))))
        {
            logger.Log(LogLevel.Warning, new EventId(1101, "StepFailed"), "Step orders failed: {ErrorCode}", "downstream_timeout");
        }

        var entry = Assert.Single(buffer.Read(0));
        Assert.Equal("abc123", entry.CorrelationId);
        Assert.Equal("customer-overview", entry.RecipeId);
        Assert.Equal("orders", entry.Step);
        Assert.Equal("service-b", entry.ServiceId);
        Assert.Equal(1101, entry.EventId);
        Assert.Equal("StepFailed", entry.EventName);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Engine.Execution.CompositionExecutor", entry.Category);
    }

    [Fact]
    public void Provider_captures_fields_of_structured_log_state()
    {
        var (factory, buffer) = Create();
        var logger = factory.CreateLogger("ApiAggregator.Engine.X");

        // Same shape as the source-generated LoggerMessage state: {Placeholder} names become fields.
        logger.LogWarning(new EventId(1101, "StepFailed"), "Step {Step} failed: service {ServiceId}, {ErrorCode}", "orders", "service-b", "downstream_timeout");

        var entry = Assert.Single(buffer.Read(0));
        Assert.Equal("orders", entry.Step);
        Assert.Equal("service-b", entry.ServiceId);
        Assert.Equal("downstream_timeout", entry.ErrorCode);
        Assert.Equal("Step orders failed: service service-b, downstream_timeout", entry.Message);
    }

    [Fact]
    public void Provider_keeps_information_from_our_own_code()
    {
        var (factory, buffer) = Create();

        factory.CreateLogger("ApiAggregator.Engine.X").LogInformation("hello");

        Assert.Equal(1, buffer.Read(0).Count);
    }

    [Fact]
    public void Provider_drops_information_from_framework_categories()
    {
        var (factory, buffer) = Create();

        factory.CreateLogger("Microsoft.AspNetCore.Foo").LogInformation("chatter");

        Assert.Empty(buffer.Read(0));
    }

    [Fact]
    public void Provider_keeps_warnings_from_framework_categories()
    {
        var (factory, buffer) = Create();

        factory.CreateLogger("Microsoft.AspNetCore.Foo").LogWarning("something to look at");

        var entry = Assert.Single(buffer.Read(0));
        Assert.Equal("Microsoft.AspNetCore.Foo", entry.Category); // only our own prefix is shortened
    }

    [Fact]
    public void Provider_records_the_exception_type_and_message_without_a_stack_trace()
    {
        var (factory, buffer) = Create();
        var logger = factory.CreateLogger("ApiAggregator.Engine.X");

        Exception thrown;
        try { throw new InvalidOperationException("the config is broken"); }
        catch (Exception ex) { thrown = ex; }
        logger.LogError(thrown, "Reload failed");

        var entry = Assert.Single(buffer.Read(0));
        Assert.Equal("InvalidOperationException: the config is broken", entry.Exception);
        Assert.False(entry.Exception!.Contains(" at "));
    }

    [Fact]
    public void Provider_cuts_a_very_long_exception_message_to_500_characters()
    {
        var (factory, buffer) = Create();

        factory.CreateLogger("ApiAggregator.Engine.X").LogError(new Exception(new string('x', 2000)), "Boom");

        Assert.Equal(500, Assert.Single(buffer.Read(0)).Exception!.Length);
    }

    [Fact]
    public void Framework_exceptions_keep_only_the_type_because_their_messages_can_hold_request_data()
    {
        var (factory, buffer) = Create();

        factory.CreateLogger("Microsoft.AspNetCore.Server").LogError(new InvalidOperationException("GET /bff/customer/C-10042?x=1 failed"), "Boom");

        Assert.Equal("InvalidOperationException", Assert.Single(buffer.Read(0)).Exception);
    }
}
