using Lumen.Core.Chat;
using Lumen.Core.Llm;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests;

public class LlmRouterTests
{
    private readonly FakeBackend _local = new(BackendKind.Local, "local ", "answer");
    private readonly FakeBackend _claude = new(BackendKind.Claude, "claude ", "answer");
    private readonly TestSettings _settings = new();

    private LlmRouter CreateRouter() => new([_local, _claude], _settings, NullLogger<LlmRouter>.Instance);

    private static Conversation Question(string text = "Hello?")
    {
        var c = new Conversation("system");
        c.Add(ChatMessage.User(text));
        return c;
    }

    private Task<List<RouterEvent>> CollectAsync(Conversation? conversation = null) =>
        CollectWithTokenAsync(conversation, TestContext.Current.CancellationToken);

    private async Task<List<RouterEvent>> CollectWithTokenAsync(Conversation? conversation, CancellationToken ct)
    {
        var events = new List<RouterEvent>();
        await foreach (RouterEvent e in CreateRouter().StreamAsync(conversation ?? Question(), ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static string TextOf(IEnumerable<RouterEvent> events) =>
        string.Concat(events.OfType<TextDelta>().Select(d => d.Text));

    [Fact]
    public async Task LocalFirst_uses_the_local_model_when_it_works()
    {
        List<RouterEvent> events = await CollectAsync();

        Assert.Equal(BackendKind.Local, Assert.IsType<BackendStarted>(events[0]).Backend);
        Assert.Equal("local answer", TextOf(events));
        Assert.Equal(BackendKind.Local, Assert.IsType<Completed>(events[^1]).Backend);
        Assert.Equal(0, _claude.Calls);
    }

    [Fact]
    public async Task CloudFirst_uses_Claude_first()
    {
        _settings.Current.RoutingMode = RoutingMode.CloudFirst;

        List<RouterEvent> events = await CollectAsync();

        Assert.Equal("claude answer", TextOf(events));
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task An_unavailable_local_model_is_skipped_and_the_reason_is_reported()
    {
        _local.UnavailableReason = "no model folder";

        List<RouterEvent> events = await CollectAsync();

        BackendStarted started = Assert.IsType<BackendStarted>(events[0]);
        Assert.Equal(BackendKind.Claude, started.Backend);
        Assert.Contains("no model folder", started.Note, StringComparison.Ordinal);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task A_failure_before_any_output_falls_back_without_discarding_anything()
    {
        _local.FailAfter = 0;

        List<RouterEvent> events = await CollectAsync();

        FallingBack fallback = Assert.Single(events.OfType<FallingBack>());
        Assert.Equal(BackendKind.Local, fallback.From);
        Assert.False(fallback.DiscardPartialOutput);
        Assert.Equal("claude answer", TextOf(events));
        Assert.Equal(BackendKind.Claude, Assert.IsType<Completed>(events[^1]).Backend);
    }

    [Fact]
    public async Task A_failure_mid_stream_tells_the_UI_to_discard_the_partial_answer()
    {
        _local.FailAfter = 1; // "local " was already streamed

        List<RouterEvent> events = await CollectAsync();

        FallingBack fallback = Assert.Single(events.OfType<FallingBack>());
        Assert.True(fallback.DiscardPartialOutput);
        int fallbackIndex = events.IndexOf(fallback);
        Assert.Equal("claude answer", TextOf(events.Skip(fallbackIndex)));
    }

    [Fact]
    public async Task LocalOnly_never_contacts_Claude_and_surfaces_the_error()
    {
        _settings.Current.RoutingMode = RoutingMode.LocalOnly;
        _local.FailAfter = 0;

        await Assert.ThrowsAsync<LlmBackendException>(() => CollectAsync());
        Assert.Equal(0, _claude.Calls);
    }

    [Fact]
    public async Task A_non_recoverable_failure_does_not_fall_back()
    {
        _settings.Current.RoutingMode = RoutingMode.CloudFirst;
        _claude.FailAfter = 0;
        _claude.FailureCanFallBack = false; // e.g. a policy refusal

        LlmBackendException ex = await Assert.ThrowsAsync<LlmBackendException>(() => CollectAsync());
        Assert.Equal(BackendKind.Claude, ex.Backend);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task No_available_backend_produces_a_helpful_error()
    {
        _local.UnavailableReason = "no model folder";
        _claude.UnavailableReason = "no API key";

        LlmBackendException ex = await Assert.ThrowsAsync<LlmBackendException>(() => CollectAsync());
        Assert.Contains("no model folder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_far_longer_than_the_local_context_goes_straight_to_Claude()
    {
        _settings.Current.Local.MaxContextTokens = 1000;

        List<RouterEvent> events = await CollectAsync(Question(new string('x', 10_000)));

        Assert.Equal(BackendKind.Claude, Assert.IsType<BackendStarted>(events[0]).Backend);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task Cancellation_is_not_treated_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectWithTokenAsync(null, cts.Token));
        Assert.Equal(0, _claude.Calls);
    }

    [Theory]
    [InlineData(RoutingMode.LocalFirst, new[] { BackendKind.Local, BackendKind.Claude })]
    [InlineData(RoutingMode.CloudFirst, new[] { BackendKind.Claude, BackendKind.Local })]
    [InlineData(RoutingMode.LocalOnly, new[] { BackendKind.Local })]
    [InlineData(RoutingMode.CloudOnly, new[] { BackendKind.Claude })]
    public void Preference_order_follows_the_mode(RoutingMode mode, BackendKind[] expected) =>
        Assert.Equal(expected, LlmRouter.PreferenceOrder(mode));
}
