using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("V0.5.0-preview.1", 0, 5, 0, "preview.1")]
    [InlineData("1.0", 1, 0, 0, "")]
    [InlineData(" 2.10.4 ", 2, 10, 4, "")]
    public void TryParse_accepts_common_forms(string text, int major, int minor, int patch, string preRelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(new SemanticVersion(major, minor, patch, preRelease), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    [InlineData("-1.0.0")]
    public void TryParse_rejects_invalid_input(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.0.0", "1.0.0-preview.1")]      // 正式版大于预览版
    [InlineData("1.0.0-preview.2", "1.0.0-preview.1")]
    [InlineData("1.0.0-preview.1", "1.0.0-preview")] // 段数多的一方更大
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    public void Comparison_orders_versions_correctly(string greater, string smaller)
    {
        Assert.True(SemanticVersion.TryParse(greater, out var a));
        Assert.True(SemanticVersion.TryParse(smaller, out var b));

        Assert.True(a.CompareTo(b) > 0, $"{a} 应当大于 {b}");
        Assert.True(b.CompareTo(a) < 0, $"{b} 应当小于 {a}");
        Assert.False(a.IsNewerThan(a));
    }

    [Fact]
    public void Equal_versions_compare_as_zero()
    {
        SemanticVersion.TryParse("0.5.0-preview.1", out var a);
        SemanticVersion.TryParse("0.5.0-preview.1", out var b);

        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ToString_round_trips()
    {
        foreach (string text in new[] { "1.2.3", "0.1.0-preview.1", "10.20.30-beta.2" })
        {
            Assert.True(SemanticVersion.TryParse(text, out var version));
            Assert.Equal(text, version.ToString());
        }
    }
}

public class UpdateManifestTests
{
    private static UpdateManifest Sample() => new()
    {
        Version = "1.0.0",
        Channel = UpdateChannels.Stable,
        Url = "https://example.com/WhSnap-1.0.0-setup-x64.exe",
        Sha256 = new string('a', 64),
        Size = 47_000_000,
        PublishedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        Notes = "首个正式版",
    };

    [Fact]
    public void Json_round_trip_keeps_every_field()
    {
        var manifest = Sample();
        var parsed = UpdateManifest.FromJson(manifest.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(manifest.Version, parsed!.Version);
        Assert.Equal(manifest.Channel, parsed.Channel);
        Assert.Equal(manifest.Url, parsed.Url);
        Assert.Equal(manifest.Sha256, parsed.Sha256);
        Assert.Equal(manifest.Size, parsed.Size);
        Assert.Equal(manifest.PublishedAtUtc, parsed.PublishedAtUtc);
        Assert.Equal(manifest.Notes, parsed.Notes);
    }

    [Fact]
    public void FromJson_rejects_garbage_and_future_schemas()
    {
        Assert.Null(UpdateManifest.FromJson("{ not json"));
        Assert.Null(UpdateManifest.FromJson("""{ "SchemaVersion": 99, "Version": "2.0.0" }"""));
    }

    [Fact]
    public void FromJson_tolerates_missing_optional_fields()
    {
        var parsed = UpdateManifest.FromJson("""{ "Version": "1.0.0", "Url": "u", "Sha256": "s" }""");

        Assert.NotNull(parsed);
        Assert.Equal(UpdateChannels.Stable, parsed!.Channel);
        Assert.Equal(string.Empty, parsed.Notes);
    }
}

public class UpdateRulesTests
{
    private static UpdateManifest Manifest(string version, string channel = UpdateChannels.Stable, string minimum = "", string url = "https://example.com/setup.exe") =>
        new()
        {
            Version = version,
            Channel = channel,
            Url = url,
            Sha256 = new string('b', 64),
            MinimumVersion = minimum,
        };

    [Fact]
    public void Newer_version_on_the_same_channel_can_update()
    {
        var decision = UpdateRules.Evaluate("0.5.0-preview.1", Manifest("1.0.0"), UpdateChannels.Stable);

        Assert.True(decision.CanUpdate);
        Assert.Contains("1.0.0", decision.Reason);
    }

    [Fact]
    public void Same_or_older_version_is_not_an_update()
    {
        Assert.False(UpdateRules.Evaluate("1.0.0", Manifest("1.0.0"), UpdateChannels.Stable).CanUpdate);
        Assert.False(UpdateRules.Evaluate("1.0.0", Manifest("0.9.0"), UpdateChannels.Stable).CanUpdate);
        Assert.False(UpdateRules.Evaluate("1.0.0", Manifest("1.0.0-preview.9"), UpdateChannels.Stable).CanUpdate);
    }

    [Fact]
    public void Stable_channel_refuses_beta_builds_but_beta_channel_accepts_both()
    {
        Assert.False(UpdateRules.Evaluate("0.5.0", Manifest("1.0.0-beta.1", UpdateChannels.Beta), UpdateChannels.Stable).CanUpdate);
        Assert.True(UpdateRules.Evaluate("0.5.0", Manifest("1.0.0-beta.1", UpdateChannels.Beta), UpdateChannels.Beta).CanUpdate);
        Assert.True(UpdateRules.Evaluate("0.5.0", Manifest("1.0.0", UpdateChannels.Stable), UpdateChannels.Beta).CanUpdate);
    }

    [Fact]
    public void Missing_url_or_hash_blocks_the_update()
    {
        Assert.False(UpdateRules.Evaluate("0.5.0", Manifest("1.0.0", url: string.Empty), UpdateChannels.Stable).CanUpdate);

        var noHash = Manifest("1.0.0") with { Sha256 = string.Empty };
        var decision = UpdateRules.Evaluate("0.5.0", noHash, UpdateChannels.Stable);

        Assert.False(decision.CanUpdate);
        Assert.Contains("SHA-256", decision.Reason);
    }

    [Fact]
    public void Minimum_version_gate_blocks_too_old_clients()
    {
        var decision = UpdateRules.Evaluate("0.3.0", Manifest("1.0.0", minimum: "0.5.0"), UpdateChannels.Stable);

        Assert.False(decision.CanUpdate);
        Assert.Contains("最低可升级版本", decision.Reason);

        Assert.True(UpdateRules.Evaluate("0.5.0", Manifest("1.0.0", minimum: "0.5.0"), UpdateChannels.Stable).CanUpdate);
    }

    [Fact]
    public void Unparsable_offered_version_is_rejected()
    {
        Assert.False(UpdateRules.Evaluate("0.5.0", Manifest("not-a-version"), UpdateChannels.Stable).CanUpdate);
    }

    [Fact]
    public void Broken_current_version_is_treated_as_upgradable()
    {
        // 当前版本号异常（例如被改坏）时，宁可允许升级把它修好
        Assert.True(UpdateRules.Evaluate("broken", Manifest("1.0.0"), UpdateChannels.Stable).CanUpdate);
    }

    [Fact]
    public void Channel_parsing_defaults_to_stable()
    {
        Assert.Equal(UpdateChannels.Beta, UpdateChannels.Parse("BETA"));
        Assert.Equal(UpdateChannels.Stable, UpdateChannels.Parse("nightly"));
        Assert.Equal(UpdateChannels.Stable, UpdateChannels.Parse(null));
    }
}

public class Sha256UtilTests
{
    [Fact]
    public void Compute_is_lowercase_hex_and_matches_the_known_digest()
    {
        // "abc" 的 SHA-256 是公开已知值，用来锁住实现不被改错
        string hash = Sha256Util.Compute("abc"u8.ToArray());

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void Matches_detects_tampering()
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-sha-" + Guid.NewGuid().ToString("N")[..8] + ".bin");

        try
        {
            File.WriteAllText(path, "hello update");
            string expected = Sha256Util.ComputeFile(path);

            Assert.True(Sha256Util.Matches(expected, path, out string actual));
            Assert.Equal(expected, actual);

            File.WriteAllText(path, "hello update!");
            Assert.False(Sha256Util.Matches(expected, path, out string tampered));
            Assert.NotEqual(expected, tampered);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Matches_is_case_insensitive_about_the_expected_hash()
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-sha-" + Guid.NewGuid().ToString("N")[..8] + ".bin");

        try
        {
            File.WriteAllText(path, "payload");
            string expected = Sha256Util.ComputeFile(path);

            Assert.True(Sha256Util.Matches(expected.ToUpperInvariant(), path, out _));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
