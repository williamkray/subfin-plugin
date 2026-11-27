using System;
using Jellyfin.Plugin.Subsonic.Auth;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class SecurityTests
{
    // ── IsShareAllowedMethod ──────────────────────────────────────────────────

    [Theory]
    [InlineData("stream", true)]
    [InlineData("download", true)]
    [InlineData("getalbumlist2", false)]
    [InlineData("createplaylist", false)]
    [InlineData("deleteplaylist", false)]
    [InlineData("getmusicdirectory", false)]
    [InlineData("search3", false)]
    [InlineData("createshare", false)]
    [InlineData("deleteshare", false)]
    [InlineData("getuser", false)]
    [InlineData("", false)]
    public void IsShareAllowedMethod_Cases(string method, bool expected)
    {
        Assert.Equal(expected, SubsonicAuth.IsShareAllowedMethod(method));
    }

    // ── SecretEquals ──────────────────────────────────────────────────────────

    [Fact]
    public void SecretEquals_Equal_True() => Assert.True(SubsonicAuth.SecretEquals("abc123", "abc123"));

    [Fact]
    public void SecretEquals_Different_False() => Assert.False(SubsonicAuth.SecretEquals("abc123", "abc124"));

    [Fact]
    public void SecretEquals_DifferentLength_False() => Assert.False(SubsonicAuth.SecretEquals("abc123", "abc1234"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SecretEquals_NullOrEmptyExpected_False(string? expected)
    {
        Assert.False(SubsonicAuth.SecretEquals(expected, "anything"));
        Assert.False(SubsonicAuth.SecretEquals(expected, expected));
    }

    [Fact]
    public void SecretEquals_NullActual_False() => Assert.False(SubsonicAuth.SecretEquals("abc", null));

    // ── IsShareExpired ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsShareExpired_NullOrEmpty_False(string? v) => Assert.False(SubsonicAuth.IsShareExpired(v));

    [Fact]
    public void IsShareExpired_PastIso_True() =>
        Assert.True(SubsonicAuth.IsShareExpired(DateTimeOffset.UtcNow.AddDays(-1).ToString("o")));

    [Fact]
    public void IsShareExpired_FutureIso_False() =>
        Assert.False(SubsonicAuth.IsShareExpired(DateTimeOffset.UtcNow.AddDays(1).ToString("o")));

    [Fact]
    public void IsShareExpired_Garbage_FailsClosed() => Assert.True(SubsonicAuth.IsShareExpired("garbage"));

    [Fact]
    public void IsShareExpired_SqliteFormat_NowAfter_True()
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 1, TimeSpan.Zero);
        Assert.True(SubsonicAuth.IsShareExpired("2030-01-01 12:00:00", now));
    }

    [Fact]
    public void IsShareExpired_SqliteFormat_NowBefore_False()
    {
        var now = new DateTimeOffset(2030, 1, 1, 11, 59, 59, TimeSpan.Zero);
        Assert.False(SubsonicAuth.IsShareExpired("2030-01-01 12:00:00", now));
    }

    // ── PlaylistAccess ────────────────────────────────────────────────────────

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static Playlist Make(Guid owner, bool open = false, params (Guid id, bool edit)[] shares)
    {
        var perms = new PlaylistUserPermissions[shares.Length];
        for (var i = 0; i < shares.Length; i++) perms[i] = new PlaylistUserPermissions(shares[i].id, shares[i].edit);
        return new Playlist { OwnerUserId = owner, OpenAccess = open, Shares = perms };
    }

    [Fact]
    public void PlaylistAccess_Owner_FullControl()
    {
        var pl = Make(Owner);
        Assert.True(PlaylistAccess.CanEdit(pl, Owner));
        Assert.True(PlaylistAccess.CanDelete(pl, Owner));
    }

    [Fact]
    public void PlaylistAccess_StrangerPrivate_NoAccess()
    {
        var pl = Make(Owner);
        Assert.False(PlaylistAccess.CanEdit(pl, Stranger));
        Assert.False(PlaylistAccess.CanDelete(pl, Stranger));
    }

    [Fact]
    public void PlaylistAccess_StrangerOpenAccess_ReadOnly()
    {
        var pl = Make(Owner, open: true);
        Assert.False(PlaylistAccess.CanEdit(pl, Stranger));
        Assert.False(PlaylistAccess.CanDelete(pl, Stranger));
    }

    [Fact]
    public void PlaylistAccess_SharedReadOnly_ReadsOnly()
    {
        var pl = Make(Owner, false, (Stranger, false));
        Assert.False(PlaylistAccess.CanEdit(pl, Stranger));
        Assert.False(PlaylistAccess.CanDelete(pl, Stranger));
    }

    [Fact]
    public void PlaylistAccess_SharedCanEdit_ReadsAndEdits_NoDelete()
    {
        var pl = Make(Owner, false, (Stranger, true));
        Assert.True(PlaylistAccess.CanEdit(pl, Stranger));
        Assert.False(PlaylistAccess.CanDelete(pl, Stranger));
    }

    [Fact]
    public void PlaylistAccess_EmptyUserVsEmptyOwner_NoAccess()
    {
        var pl = Make(Guid.Empty);
        Assert.False(PlaylistAccess.CanEdit(pl, Guid.Empty));
        Assert.False(PlaylistAccess.CanDelete(pl, Guid.Empty));
    }

    [Fact]
    public void PlaylistAccess_EmptyUserVsEmptyOwner_OpenAccess_ReadOnly()
    {
        var pl = Make(Guid.Empty, open: true);
        Assert.False(PlaylistAccess.CanEdit(pl, Guid.Empty));
        Assert.False(PlaylistAccess.CanDelete(pl, Guid.Empty));
    }
}
