using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Xunit;

namespace Smx.Domain.Tests;

public class InMemoryIntakeSessionStoreTests
{
    [Fact]
    public async Task RoundTrips_ASession()
    {
        var store = new InMemoryIntakeSessionStore();
        var id = RecordIds.NewIntakeSessionId();
        await store.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, Client = "Acme", CreatedAt = "2026-07-21T10:00:00.0000000Z",
        });

        var back = await store.GetAsync(id);
        Assert.Equal("Acme", back!.Client);
    }

    [Fact]
    public async Task Returns_NullForAnUnknownSession() =>
        Assert.Null(await new InMemoryIntakeSessionStore().GetAsync("isx-nope"));
}
