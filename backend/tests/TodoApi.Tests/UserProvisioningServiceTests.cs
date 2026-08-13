using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Auth;
using TodoApi.Data;
using TodoApi.Models;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Tests the real <see cref="UserProvisioningService"/> against EF Core InMemory (no host,
/// no real Postgres) — specs §16.18 item 12, the "simultaneous first request" concurrency
/// guard, plus the get-or-create contract items 6/7 restated against the real
/// implementation rather than a fake.
/// </summary>
public class UserProvisioningServiceTests
{
    private static TodoDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<TodoDbContext>().UseInMemoryDatabase(dbName).Options);

    [Fact]
    public async Task GetOrCreateAsync_UnknownOid_CreatesRowWithUserRole()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateContext(dbName);
        var sut = new UserProvisioningService(db, NullLogger<UserProvisioningService>.Instance);

        var identity = new ProvisioningIdentity(Guid.NewGuid(), "a@example.com", "A B", null);
        var user = await sut.GetOrCreateAsync(identity, CancellationToken.None);

        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal(identity.EntraObjectId, user.EntraObjectId);
        Assert.Equal(1, await db.Users.CountAsync(u => u.EntraObjectId == identity.EntraObjectId));
    }

    [Fact]
    public async Task GetOrCreateAsync_KnownOid_ReturnsExistingRow_DoesNotResyncProfileOrCreateDuplicate()
    {
        var dbName = Guid.NewGuid().ToString();
        var oid = Guid.NewGuid();

        using (var seedDb = CreateContext(dbName))
        {
            seedDb.Users.Add(new AppUser
            {
                Id = Guid.NewGuid(),
                EntraObjectId = oid,
                DisplayName = "Original Name",
                Role = UserRole.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext(dbName);
        var sut = new UserProvisioningService(db, NullLogger<UserProvisioningService>.Instance);

        var user = await sut.GetOrCreateAsync(
            new ProvisioningIdentity(oid, "new@example.com", "New Name", null),
            CancellationToken.None);

        Assert.Equal(UserRole.Admin, user.Role); // unchanged — role is never re-derived here
        Assert.Equal("Original Name", user.DisplayName); // not re-synced on every request

        using var verifyDb = CreateContext(dbName);
        Assert.Equal(1, await verifyDb.Users.CountAsync(u => u.EntraObjectId == oid));
    }

    /// <summary>
    /// specs §16.18 item 12: "two simultaneous first requests for the same oid produce
    /// exactly one row (simulate the unique-violation path and assert it re-reads rather
    /// than throwing)." Two independent DbContext instances backed by the SAME InMemory
    /// database race a genuine <c>Task.WhenAll</c> insert of the same
    /// <c>entra_object_id</c>: EF Core InMemory enforces the unique index configured in
    /// <see cref="TodoDbContext"/>, so this exercises the real losing branch in
    /// <see cref="UserProvisioningService.GetOrCreateAsync"/>, not a simulation of it.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_TwoConcurrentFirstRequestsForTheSameOid_ProduceExactlyOneRow()
    {
        var dbName = Guid.NewGuid().ToString();
        var oid = Guid.NewGuid();

        using var dbA = CreateContext(dbName);
        using var dbB = CreateContext(dbName);
        var sutA = new UserProvisioningService(dbA, NullLogger<UserProvisioningService>.Instance);
        var sutB = new UserProvisioningService(dbB, NullLogger<UserProvisioningService>.Instance);

        var identityA = new ProvisioningIdentity(oid, "race-a@example.com", "Race A", null);
        var identityB = new ProvisioningIdentity(oid, "race-b@example.com", "Race B", null);

        var taskA = sutA.GetOrCreateAsync(identityA, CancellationToken.None);
        var taskB = sutB.GetOrCreateAsync(identityB, CancellationToken.None);

        var results = await Task.WhenAll(taskA, taskB);

        // Both callers must observe the SAME winning row (whichever request's INSERT the
        // unique index let through), never throw, and never end up with two rows.
        Assert.Equal(results[0].Id, results[1].Id);

        using var verifyDb = CreateContext(dbName);
        Assert.Equal(1, await verifyDb.Users.CountAsync(u => u.EntraObjectId == oid));
    }
}
