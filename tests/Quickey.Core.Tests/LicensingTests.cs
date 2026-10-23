using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Quickey.Core.Licensing;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class LicensingTests : IDisposable
{
    private static readonly LicenseProduct Product = new(StoreId: 111, ProductId: 222);

    private readonly TempFolder _temp = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeHandler _server = new();
    private readonly MemoryStore _store = new();

    public void Dispose() => _temp.Dispose();

    private LicenseManager Manager() => new(new LemonSqueezyClient(new HttpClient(_server), Product, _clock), _store, _clock);

    private const string ActivatedOurs = """
        {"activated":true,"error":null,"license_key":{"status":"active"},"instance":{"id":"inst-1"},
         "meta":{"store_id":111,"product_id":222,"customer_email":"buyer@example.com"}}
        """;

    [Fact]
    public async Task Activating_a_good_key_makes_the_pc_pro()
    {
        _server.Reply(ActivatedOurs);
        var manager = Manager();

        var result = await manager.ActivateAsync("  KEY-123 ", "My-PC");

        Assert.True(result.Success);
        Assert.True(manager.IsPro);
        Assert.Equal(new LicenseRecord("KEY-123", "inst-1", "buyer@example.com", _clock.GetUtcNow()), _store.Record);
        Assert.Contains("license_key=KEY-123", _server.Bodies[0]);
    }

    [Fact]
    public async Task Rejects_and_releases_a_key_from_another_product()
    {
        _server.Reply("""{"activated":true,"instance":{"id":"inst-9"},"meta":{"store_id":111,"product_id":999}}""");
        _server.Reply("""{"deactivated":true}""");
        var manager = Manager();

        var result = await manager.ActivateAsync("OTHER", "My-PC");

        Assert.False(result.Success);
        Assert.False(manager.IsPro);
        Assert.EndsWith("/deactivate", _server.Paths[1]);
    }

    [Fact]
    public async Task Surfaces_the_server_error_for_a_bad_key()
    {
        _server.Reply("""{"activated":false,"error":"license_key not found."}""", HttpStatusCode.NotFound);

        var result = await Manager().ActivateAsync("NOPE", "My-PC");

        Assert.Equal("license_key not found.", result.Error);
    }

    [Fact]
    public async Task Keeps_working_offline_within_the_grace_period_then_lapses()
    {
        _store.Record = new LicenseRecord("KEY", "inst-1", null, _clock.GetUtcNow());
        var manager = Manager();
        _server.Fail();

        _clock.Advance(TimeSpan.FromDays(10));
        await manager.RefreshAsync();
        Assert.True(manager.IsPro);

        _clock.Advance(TimeSpan.FromDays(5));
        await manager.RefreshAsync();
        Assert.False(manager.IsPro);
    }

    [Fact]
    public async Task A_revoked_key_is_forgotten_on_the_next_check()
    {
        _store.Record = new LicenseRecord("KEY", "inst-1", null, _clock.GetUtcNow());
        var manager = Manager();
        _server.Reply("""{"valid":false,"error":"license_key disabled"}""");

        _clock.Advance(TimeSpan.FromDays(2));
        await manager.RefreshAsync();

        Assert.False(manager.IsPro);
        Assert.Null(_store.Record);
    }

    [Fact]
    public async Task Does_not_recheck_more_than_once_a_day()
    {
        _store.Record = new LicenseRecord("KEY", "inst-1", null, _clock.GetUtcNow());
        var manager = Manager();

        _clock.Advance(TimeSpan.FromHours(5));
        await manager.RefreshAsync();

        Assert.Empty(_server.Paths);
    }

    [Fact]
    public void Free_tier_allows_ten_files_a_day_and_resets_at_midnight()
    {
        var usage = new UsageTracker(_temp.File("usage.json"), _clock);

        Assert.True(usage.TryConsume(7));
        Assert.False(usage.TryConsume(4));
        Assert.Equal(3, usage.RemainingToday);
        Assert.True(usage.TryConsume(3));
        Assert.Equal(0, usage.RemainingToday);

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(UsageTracker.FreeDailyConversions, usage.RemainingToday);
    }

    [Fact]
    public void Actions_are_free_and_pro_is_unlimited()
    {
        var usage = new UsageTracker(_temp.File("usage.json"), _clock);
        usage.TryConsume(UsageTracker.FreeDailyConversions);
        var free = new Entitlements(Manager(), usage);
        var convert = new StubOperation(countsTowardLimit: true);
        var action = new StubOperation(countsTowardLimit: false);

        Assert.False(free.CanRun(convert, 1));
        Assert.True(free.CanRun(action, 50));

        _store.Record = new LicenseRecord("KEY", "inst-1", null, _clock.GetUtcNow());
        var pro = new Entitlements(Manager(), usage);
        Assert.True(pro.CanRun(convert, 500));
    }

    [Fact]
    public void Charges_only_succeeded_files_and_never_past_the_allowance()
    {
        var usage = new UsageTracker(_temp.File("usage.json"), _clock);
        var free = new Entitlements(Manager(), usage);
        var convert = new StubOperation(countsTowardLimit: true);

        free.Charge(convert, 4);
        Assert.Equal(6, usage.RemainingToday);

        free.Charge(convert, 50);
        Assert.Equal(0, usage.RemainingToday);
        Assert.Equal(UsageTracker.FreeDailyConversions, usage.UsedToday);
    }

    private sealed class MemoryStore : ILicenseStore
    {
        public LicenseRecord? Record;
        public LicenseRecord? Load() => Record;
        public void Save(LicenseRecord record) => Record = record;
        public void Clear() => Record = null;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _replies = new();
        private bool _down;
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];

        public void Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            _replies.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        public void Fail() => _down = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (_down)
                throw new HttpRequestException("offline");
            return _replies.Dequeue()();
        }
    }

    private sealed class StubOperation(bool countsTowardLimit) : IOperation
    {
        public string Id => "stub";
        public string Label => "Stub";
        public string Glyph => "";
        public OperationCategory Category => OperationCategory.Convert;
        public bool CountsTowardLimit => countsTowardLimit;
        public bool AppliesTo(IReadOnlyList<string> files) => true;
        public Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult(Label));
    }
}
