using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class PinBackupTests
{
    [Fact]
    public void RectCodec_round_trips_including_negative_origins()
    {
        var rect = new PhysicalRect(-1280, -100, 3200, 1080);

        string text = RectCodec.Format(rect);

        Assert.Equal("-1280,-100,3200,1080", text);
        Assert.True(RectCodec.TryParse(text, out var parsed));
        Assert.Equal(rect, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("1,2,3,4,5")]
    public void RectCodec_rejects_malformed_input(string text)
    {
        Assert.False(RectCodec.TryParse(text, out _));
    }

    [Fact]
    public void Entry_round_trips_through_state()
    {
        var state = PinState.ForImage(640, 480, new PhysicalPoint(120, 340)) with
        {
            Zoom = 1.25,
            Rotation = PinRotation.Clockwise90,
            FlipHorizontal = true,
            Opacity = 70,
            Thumbnail = true,
            ClickThrough = true,
        };

        var entry = PinBackupEntry.FromState(state, "pin-000.png", 3);
        var restored = entry.ToState(640, 480);

        Assert.Equal(state.Position, restored.Position);
        Assert.Equal(state.Zoom, restored.Zoom);
        Assert.Equal(state.Rotation, restored.Rotation);
        Assert.Equal(state.FlipHorizontal, restored.FlipHorizontal);
        Assert.Equal(state.FlipVertical, restored.FlipVertical);
        Assert.Equal(state.Opacity, restored.Opacity);
        Assert.Equal(state.Thumbnail, restored.Thumbnail);
        Assert.Equal(state.ClickThrough, restored.ClickThrough);
        Assert.Equal(3, entry.ZIndex);
    }

    [Fact]
    public void Manifest_json_round_trips_and_sorts_by_z_index()
    {
        var manifest = new PinBackupManifest
        {
            Desktop = RectCodec.Format(new PhysicalRect(0, 0, 1920, 1080)),
            SavedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Pins =
            [
                PinBackupEntry.FromState(PinState.ForImage(10, 10, new PhysicalPoint(5, 5)), "b.png", 2),
                PinBackupEntry.FromState(PinState.ForImage(10, 10, new PhysicalPoint(1, 1)), "a.png", 0),
            ],
        };

        var parsed = PinBackupManifest.FromJson(manifest.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(manifest.Desktop, parsed!.Desktop);
        Assert.Equal(manifest.SavedAtUtc, parsed.SavedAtUtc);
        Assert.Equal(["a.png", "b.png"], parsed.Pins.Select(p => p.File));
    }

    [Fact]
    public void Manifest_rejects_invalid_json_instead_of_throwing()
    {
        Assert.Null(PinBackupManifest.FromJson("{ not json"));
    }

    [Fact]
    public void Manifest_refuses_future_schema_versions()
    {
        string json = """{ "SchemaVersion": 99, "Pins": [] }""";

        Assert.Null(PinBackupManifest.FromJson(json));
    }

    [Fact]
    public void Retention_keeps_the_newest_sessions()
    {
        var sessions = new[] { "s3", "s2", "s1" };

        Assert.Equal(["s1"], PinBackupRetention.Victims(sessions, 2));
        Assert.Empty(PinBackupRetention.Victims(sessions, 5));
        Assert.Equal(["s2", "s1"], PinBackupRetention.Victims(sessions, 1));
    }

    [Fact]
    public void Retention_always_keeps_at_least_one_session()
    {
        // keep=0 会被夹到 1：绝不能因为参数写错把用户所有贴图备份删光
        Assert.Empty(PinBackupRetention.Victims(["s1"], 0));
    }
}
