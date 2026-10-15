using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class SettingsSchemaTests
{
    [Fact]
    public void Schema_passes_its_own_integrity_check()
    {
        Assert.Empty(SettingsSchema.Validate());
    }

    [Fact]
    public void Keys_are_unique_and_groups_are_populated()
    {
        var keys = SettingsSchema.All.Select(setting => setting.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (string group in SettingsSchema.Groups)
        {
            Assert.NotEmpty(SettingsSchema.InGroup(group));
        }
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        Assert.NotNull(SettingsSchema.Find("overlaydimopacity"));
        Assert.Null(SettingsSchema.Find("NoSuchSetting"));
        Assert.Null(SettingsSchema.Find(null));
    }

    [Fact]
    public void Every_setting_has_a_label_and_description_slot()
    {
        Assert.All(SettingsSchema.All, setting =>
        {
            Assert.False(string.IsNullOrWhiteSpace(setting.Label));
            Assert.NotNull(setting.Description);
        });
    }

    [Fact]
    public void Ranged_settings_have_sane_bounds()
    {
        foreach (var setting in SettingsSchema.All.Where(setting => setting.HasRange))
        {
            Assert.True(setting.Max > setting.Min, $"{setting.Key} 范围非法。");
        }
    }
}

public class SettingsImportTests
{
    private static SettingsImportResult Validate(params (string Key, string? Value)[] values) =>
        SettingsImportValidator.Validate(values.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public void Valid_values_are_accepted()
    {
        var result = Validate(
            ("CaptureBackend", "dxgi"),
            ("OverlayDimOpacity", "60"),
            ("PinZoomStep", "1.25"),
            ("PinBackupEnabled", "true"),
            ("TextPinForeground", "#FF8040"));

        Assert.Equal(5, result.Applied.Count);
        Assert.False(result.HasProblems);
    }

    [Fact]
    public void Unknown_keys_are_reported_not_silently_dropped()
    {
        var result = Validate(("TotallyUnknown", "1"), ("CaptureBackend", "auto"));

        Assert.Equal(["TotallyUnknown"], result.Unknown);
        Assert.Contains("CaptureBackend", result.Applied);
        Assert.True(result.HasProblems);
        Assert.Contains("忽略未知项 1 个", result.Describe());
    }

    [Fact]
    public void Out_of_range_and_malformed_values_are_reported()
    {
        var result = Validate(
            ("OverlayDimOpacity", "999"),
            ("CaptureBackend", "vulkan"),
            ("PinBackupEnabled", "maybe"),
            ("TextPinForeground", "not-a-color"),
            ("PinZoomStep", "abc"));

        Assert.Empty(result.Applied);
        Assert.Equal(5, result.Invalid.Count);
    }

    [Fact]
    public void Bool_and_number_normalization_is_applied()
    {
        Assert.True(SettingsImportValidator.IsValid(SettingsSchema.Find("PinBackupEnabled")!, "TRUE", out string flag));
        Assert.Equal("true", flag);

        Assert.True(SettingsImportValidator.IsValid(SettingsSchema.Find("OverlayDimOpacity")!, " 45 ", out string number));
        Assert.Equal("45", number);

        Assert.True(SettingsImportValidator.IsValid(SettingsSchema.Find("PinZoomStep")!, "1.1000", out string zoom));
        Assert.Equal("1.1", zoom);

        Assert.True(SettingsImportValidator.IsValid(SettingsSchema.Find("CaptureBackend")!, "DXGI", out string choice));
        Assert.Equal("dxgi", choice);
    }

    [Fact]
    public void Empty_directory_is_valid_and_means_default_folder()
    {
        Assert.True(SettingsImportValidator.IsValid(SettingsSchema.Find("SaveDirectory")!, string.Empty, out _));
    }

    [Fact]
    public void Case_insensitive_lookup_accepts_json_casing()
    {
        var result = Validate(("capturebackend", "gdi"));

        Assert.Single(result.Applied);
        Assert.Empty(result.Unknown);
    }
}
