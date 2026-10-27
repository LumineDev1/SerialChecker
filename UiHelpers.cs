using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SerialChecker;

/// <summary>Display metadata shared by the main and comparison windows.</summary>
public static class CategoryVisuals
{
    public static string Icon(string? categoryName) => categoryName switch
    {
        "System Information" => "\uE770",
        "Operating System" => "\uE7F8",
        "BIOS Information" => "\uEC7A",
        "TPM Information" => "\uE72E",
        "Baseboard Information" => "\uE950",
        "Processor Information" => "\uE9D9",
        "Chassis/Enclosure Information" => "\uE7F8",
        "Physical Memory (RAM)" => "\uE7B8",
        "Disk Drive Information" => "\uEDA2",
        "Volume Serial Numbers" => "\uE8B7",
        "Network Information" => "\uE968",
        "ARP Information" => "\uE774",
        "Monitor Information" => "\uE7F4",
        "GPU Information" => "\uE7FC",
        "USB Peripherals" => "\uE88E",
        _ => "\uE946"
    };

    public static string Group(string? categoryName) => categoryName switch
    {
        "System Information" or "Operating System" => "System identity",
        "BIOS Information" or "TPM Information" => "Firmware & trust",
        "Baseboard Information" or "Processor Information" or "Chassis/Enclosure Information" or "Physical Memory (RAM)" => "Core hardware",
        "Disk Drive Information" or "Volume Serial Numbers" => "Storage",
        "Network Information" or "ARP Information" => "Network",
        "Monitor Information" or "GPU Information" => "Display",
        "USB Peripherals" => "Peripherals",
        _ => "Other"
    };

    public static string Description(string? categoryName) => categoryName switch
    {
        "System Information" => "SMBIOS system details and IDs.",
        "Operating System" => "Windows version, install details, and machine IDs.",
        "BIOS Information" => "Firmware details and security settings.",
        "TPM Information" => "TPM status, firmware, certificate serial, and thumbprint.",
        "Baseboard Information" => "Motherboard model, revision, asset tag, and serial.",
        "Processor Information" => "Processor model, topology, and reported IDs.",
        "Chassis/Enclosure Information" => "Enclosure details and asset IDs.",
        "Physical Memory (RAM)" => "Memory modules, slots, and serials.",
        "Disk Drive Information" => "Drive IDs read through each supported query path.",
        "Volume Serial Numbers" => "Volume IDs visible to Windows.",
        "Network Information" => "Adapter settings, kernel MAC, and cached MAC.",
        "ARP Information" => "Entries currently visible in the ARP table.",
        "Monitor Information" => "Monitor IDs decoded from EDID.",
        "GPU Information" => "Graphics adapter IDs and driver details.",
        "USB Peripherals" => "Current and disconnected USB device records.",
        _ => "Collected hardware and system values."
    };

    /// <summary>Name for one block of a category that is split by separators (e.g. "Drive 2").</summary>
    public static string BlockNoun(string? categoryName) => categoryName switch
    {
        "Disk Drive Information" => "Drive",
        "Physical Memory (RAM)" => "Module",
        "GPU Information" => "Adapter",
        "Monitor Information" => "Monitor",
        _ => "Group"
    };

    /// <summary>True for values that carry no unique ID (zeros, placeholders, unavailable).</summary>
    public static bool IsMutedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string trimmed = value.Trim();
        int alnum = 0;
        bool allZero = true;
        foreach (char c in trimmed)
        {
            if (!char.IsLetterOrDigit(c))
                continue;
            alnum++;
            if (c != '0')
            {
                allZero = false;
                break;
            }
        }
        if (allZero && alnum >= 4)
            return true;

        return trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("GPU-Unknown", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Requires Admin", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("—", StringComparison.Ordinal) ||
               trimmed.StartsWith("To be filled", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Not ", StringComparison.OrdinalIgnoreCase);
    }

    public const string GhostMarker = " [GHOST]";
}

/// <summary>Maps a category name to its sidebar section (used as a PropertyGroupDescription converter).</summary>
public sealed class CategoryGroupConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CategoryVisuals.Group(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CategoryIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CategoryVisuals.Icon(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Primary text for real IDs, tertiary for placeholders/zeros so real values stand out.</summary>
public sealed class MutedValueBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = CategoryVisuals.IsMutedValue(value as string) ? "Text.Tertiary" : "Text.Primary";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Strips the " [GHOST]" suffix for display; the model label keeps it.</summary>
public sealed class GhostLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string label = value as string ?? "";
        return label.EndsWith(CategoryVisuals.GhostMarker, StringComparison.Ordinal)
            ? label[..^CategoryVisuals.GhostMarker.Length]
            : label;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class IsGhostConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.Contains("[GHOST]", StringComparison.Ordinal) == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
