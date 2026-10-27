## Serial Checker 2.0.1

### New: export your comparison results

The Compare window now has an **Export results** button (or press **Ctrl+E**). It saves the checks you're looking at:

- **Text report:** a readable summary with the baseline date, pass rate, and every check's baseline value, current value, and verdict, grouped by category
- **CSV:** one row per check (category, check, expectation, baseline, current, changed, outcome, result, detail), ready for Excel or Google Sheets

The export follows your current view: the **Important** or **All values** tab, an outcome filter (for example only **Attention**), and any search. Clear the filters first to export everything.

### Fixed: more honest pass rates

- **ARP:** when a scan reads no ARP entries at all, the baseline entries now show as **Unavailable** instead of each one counting as a pass. Broadcast and multicast entries, which are the same on every PC, are no longer scored, and a newly seen device is shown for context only.
- **MAC addresses:** adapters that share one MAC (such as a virtual switch and its network card) are listed once, so one address is no longer scored several times and can't push a second adapter out of the list.
- **BIOS Serial:** Windows reads it from the same SMBIOS field as System Serial, so while the two match it's scored once, under System Serial.
- **Placeholder serials:** `9876543210` (reported by ASUS Aura controllers) and `1234567890` now count as placeholders.

### Download

Grab `SerialChecker-2.0.1.exe` below. It's a single self-contained file with no install needed.
