## Serial Checker 2.0.0

The tool formerly known as **spoofed?** is now **Serial Checker**, with a redesigned interface and a faster scan.

### Download

Grab `SerialChecker-2.0.0.exe` below. It's a single self-contained file, so no .NET install is needed. It asks for administrator rights on launch so it can read TPM, disk, and network IDs.

### New

- **Redesigned interface:** sidebar grouped by area, a card for each drive, RAM module, and GPU, and placeholder values dimmed so real IDs stand out
- **All serials page:** every value from the scan on one page
- **Per-tab baselines:** **Save baseline** on All serials saves every tab, and **Save tab baseline** inside a tab updates only that tab. Compare checks only the tabs that have a saved baseline.
- **Compare window:** pass-rate summary, clickable outcome filters, search, and a baseline → current view for every check
- **Search everything** with Ctrl+F
- **Five colour themes:** Midnight, Ocean, Forest, Ember, Sakura
- **Copy buttons** on every value, plus **Rescan** (F5)
- **Shortcuts:** Ctrl+S saves a baseline and Ctrl+E exports a report
- **`--no-elevate`** launch option for limited mode without the admin prompt

### Faster

- All 14 checks run at the same time. Without admin a full scan takes well under a second.
- Hardware-accelerated window rendering and a virtualized list for long USB and comparison lists

### Fixed

- **Virtualization** showed "Enabled" on every PC. It now reads the real state.
- **Family Serial** showed the SKU number. It now reads the SMBIOS system family.
- A connected USB device could be listed as a ghost because of a letter-case mismatch, so cleanup could remove it.
- Ghost cleanup no longer runs a temporary script file, and device IDs are escaped.
- External tools (`arp`, `nvidia-smi`, PowerShell, Explorer) are started from their Windows paths, never from the app's folder.
- Ghost cleanup no longer freezes the window.
- A missing Windows install date no longer hides Machine GUID and Product ID.
- A hung `arp` or `nvidia-smi` can no longer stall the scan.

### Upgrading

- **Baselines:** your existing baseline is copied over automatically on first launch. Data now lives in `%AppData%\Serial Checker`.
- **Re-save your baseline after updating.** Virtualization and Family Serial now report real values, so an old baseline would flag them as changed.
