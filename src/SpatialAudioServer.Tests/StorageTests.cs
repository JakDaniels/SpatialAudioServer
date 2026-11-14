using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Migrations_bring_a_new_database_to_the_latest_version_and_are_idempotent()
    {
        var path = _dir.File("db.sqlite");
        Assert.Equal(Migrations.LatestVersion, new Database(path).SchemaVersion);
        Assert.Equal(Migrations.LatestVersion, new Database(path).SchemaVersion); // re-open: nothing to apply
    }

    [Fact]
    public void Rooms_round_trip()
    {
        var store = new RoomStore(new Database(_dir.File("db.sqlite")));
        var room = new RoomRecord(Guid.NewGuid(), "Lobby", "region-1", 8, """{"a":1}""", _time.GetUtcNow());
        store.Insert(room);

        Assert.Equal(room, Assert.Single(store.All()));
        Assert.True(store.Delete(room.Id));
        Assert.Empty(store.All());
    }

    [Fact]
    public void Join_token_is_redeemable_once()
    {
        var tokens = new JoinTokenStore(new Database(_dir.File("db.sqlite")), _time);
        var pid = Guid.NewGuid();
        var room = Guid.NewGuid();
        var (token, _) = tokens.Issue(pid, room, TimeSpan.FromMinutes(5));

        Assert.Equal(new JoinTokenClaim(pid, room), tokens.Redeem(token));
        Assert.Null(tokens.Redeem(token));
        Assert.Null(tokens.Redeem("not-a-token"));
    }

    [Fact]
    public void Join_token_expires()
    {
        var tokens = new JoinTokenStore(new Database(_dir.File("db.sqlite")), _time);
        var (token, expires) = tokens.Issue(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5));
        _time.Now = expires;

        Assert.Null(tokens.Redeem(token));
    }

    [Fact]
    public void Purging_returns_expired_unused_tokens_and_keeps_live_ones()
    {
        var tokens = new JoinTokenStore(new Database(_dir.File("db.sqlite")), _time);
        var room = Guid.NewGuid();
        var (used, _) = tokens.Issue(Guid.NewGuid(), room, TimeSpan.FromMinutes(1));
        var neverUsed = Guid.NewGuid();
        tokens.Issue(neverUsed, room, TimeSpan.FromMinutes(1));
        var (live, _) = tokens.Issue(Guid.NewGuid(), room, TimeSpan.FromMinutes(10));
        Assert.NotNull(tokens.Redeem(used));
        Assert.Empty(tokens.PurgeDead()); // nothing has expired yet (the used one is just deleted)

        _time.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(new JoinTokenClaim(neverUsed, room), Assert.Single(tokens.PurgeDead()));
        Assert.Empty(tokens.PurgeDead());
        Assert.NotNull(tokens.Peek(live));
    }

    [Fact]
    public void Api_keys_are_stored_hashed_and_can_be_revoked()
    {
        var store = new ApiKeyStore(new Database(_dir.File("db.sqlite")), _time);
        var (record, key) = store.Create("region module");

        Assert.StartsWith(ApiKeyStore.KeyPrefix, key);
        Assert.Equal(record.Id, store.FindActive(Secrets.Hash(key))?.Id);
        Assert.Equal(1, store.Revoke(record.Prefix));
        Assert.Null(store.FindActive(Secrets.Hash(key)));
        Assert.NotNull(Assert.Single(store.List()).RevokedAt);
    }
}
