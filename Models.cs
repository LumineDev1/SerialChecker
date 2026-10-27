using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SerialChecker.Models
{
    public readonly record struct AuditProgress(int Completed, int Total, string CategoryName);

    public class AuditCategory
    {
        public string Name { get; set; } = "";
        public List<AuditItem> Items { get; set; } = new List<AuditItem>();
        public int DisplayItemCount => Items.Count(item => !item.IsSeparator);

        /// <summary>When this category was saved into the baseline. Null for live scans and older baselines.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? SavedAt { get; set; }
    }

    public class AuditItem
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
        public string TooltipText { get; set; } = "";
        public bool IsSeparator { get; set; }
        public bool IsPlaceholder { get; set; }
    }
}

