using System.IO;
using System.Text.Json;
using SerialChecker.Models;

namespace SerialChecker;

/// <summary>
/// The saved baseline: a list of categories in baseline.json. Every category carries its own
/// save time, so one tab can be re-saved without touching the others.
/// </summary>
internal static class BaselineStore
{
    public static string Folder { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Serial Checker");
    public static string FilePath => Path.Combine(Folder, "baseline.json");

    // Versions before the Serial Checker name stored data here.
    private static readonly string LegacyFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SystemHardwareAudit");

    /// <summary>
    /// Copies the baseline and settings from the pre-rename folder on first run, so an existing
    /// baseline keeps working. Copies rather than moves, leaving older versions untouched.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (!Directory.Exists(LegacyFolder))
                return;

            Directory.CreateDirectory(Folder);
            foreach (string name in new[] { "baseline.json", "settings.json" })
            {
                string from = Path.Combine(LegacyFolder, name);
                string to = Path.Combine(Folder, name);
                if (File.Exists(from) && !File.Exists(to))
                    File.Copy(from, to);
            }
        }
        catch
        {
            // Without the old files the app simply starts with no baseline.
        }
    }

    // Older app versions stored TPM rows inside BIOS Information.
    private static readonly HashSet<string> LegacyTpmLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "TPM Status",
        "Manufacturer ID",
        "Manufacturer Version",
        "Spec Version",
        "Endorsement Key",
        "Endorsement Key Serial Number",
        "Endorsement Key Thumbprint"
    };

    public static bool Exists => File.Exists(FilePath);

    /// <summary>Loads the baseline, or null when none is saved. Throws if the file is unreadable.</summary>
    public static List<AuditCategory>? Load()
    {
        if (!Exists)
            return null;

        List<AuditCategory>? data = JsonSerializer.Deserialize<List<AuditCategory>>(File.ReadAllText(FilePath));
        if (data == null)
            return null;

        // Baselines from older versions have no per-category time; use the file's.
        DateTime fileTime = File.GetLastWriteTime(FilePath);
        foreach (AuditCategory category in data)
            category.SavedAt ??= fileTime;

        return data;
    }

    /// <summary>Save times by category name; empty when there is no readable baseline.</summary>
    public static Dictionary<string, DateTime> LoadSaveTimes()
    {
        try
        {
            return (Load() ?? new List<AuditCategory>())
                .GroupBy(category => category.Name)
                .ToDictionary(group => group.Key, group => group.Last().SavedAt ?? DateTime.MinValue);
        }
        catch
        {
            return new Dictionary<string, DateTime>();
        }
    }

    /// <summary>Replaces the whole baseline with these categories.</summary>
    public static void SaveAll(IEnumerable<AuditCategory> categories)
    {
        DateTime now = DateTime.Now;
        Write(categories.Select(category => Stamp(category, now)).ToList());
    }

    /// <summary>
    /// Saves one category into the baseline and keeps every other saved category as it was.
    /// <paramref name="categoryOrder"/> keeps the file in the same order as the scan.
    /// </summary>
    public static void SaveCategory(AuditCategory category, IReadOnlyList<string> categoryOrder)
    {
        List<AuditCategory> saved;
        try
        {
            saved = Load() ?? new List<AuditCategory>();
        }
        catch (JsonException)
        {
            saved = new List<AuditCategory>(); // unreadable baseline: start a fresh one
        }

        saved.RemoveAll(existing => existing.Name == category.Name);
        if (category.Name == "TPM Information")
        {
            foreach (AuditCategory bios in saved.Where(existing => existing.Name == "BIOS Information"))
                bios.Items.RemoveAll(item => LegacyTpmLabels.Contains(item.Label ?? ""));
        }

        saved.Add(Stamp(category, DateTime.Now));

        int OrderOf(AuditCategory c)
        {
            int index = -1;
            for (int i = 0; i < categoryOrder.Count; i++)
            {
                if (categoryOrder[i] == c.Name)
                {
                    index = i;
                    break;
                }
            }
            return index < 0 ? int.MaxValue : index;
        }

        Write(saved.OrderBy(OrderOf).ToList());
    }

    private static AuditCategory Stamp(AuditCategory category, DateTime savedAt) => new AuditCategory
    {
        Name = category.Name,
        Items = category.Items,
        SavedAt = savedAt
    };

    private static void Write(List<AuditCategory> categories)
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(categories));
        File.Move(temp, FilePath, overwrite: true);
    }
}
