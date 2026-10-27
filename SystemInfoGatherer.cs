using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using SerialChecker.Models;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SerialChecker
{
    public static class SystemInfoGatherer
    {
        private const int ProcessTimeoutMilliseconds = 5000;
        private const string UsbCategoryName = "USB Peripherals";

        private static readonly Regex UsbVidPidRegex = new Regex(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.Compiled);
        private static readonly Lazy<bool> AdministratorCheck = new Lazy<bool>(CheckIsAdministrator);

        // Order matters: it is the sidebar order and the order saved in baselines.
        private static readonly (string Name, Func<AuditCategory> Collect)[] Collectors =
        {
            ("System Information", GetIdentityAndCryptography),
            ("Operating System", GetOperatingSystem),
            ("BIOS Information", GetFirmwareAndSecurity),
            ("TPM Information", GetTpmInformation),
            ("Baseboard Information", GetMotherboard),
            ("Processor Information", GetProcessor),
            ("Physical Memory (RAM)", GetMemory),
            ("Disk Drive Information", GetStorage),
            ("Volume Serial Numbers", GetVolumes),
            ("Network Information", GetNetworkInterfaces),
            ("ARP Information", GetArpInformation),
            ("Monitor Information", GetMonitors),
            ("GPU Information", GetGraphics),
            (UsbCategoryName, GetUsbPeripherals)
        };

        public static bool IsAdministrator => AdministratorCheck.Value;

        public static List<AuditCategory> GetSystemAudit(IProgress<AuditProgress>? progress = null)
        {
            int total = Collectors.Length;
            int completed = 0;
            var results = new AuditCategory[total];
            var tasks = new Task[total];

            // Collectors block on WMI, IOCTLs, and child processes, so each gets its own thread
            // instead of waiting for the thread pool to grow.
            for (int i = 0; i < total; i++)
            {
                int index = i;
                tasks[index] = Task.Factory.StartNew(() =>
                {
                    AuditCategory category = RunCollector(Collectors[index].Name, Collectors[index].Collect);
                    results[index] = category;
                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new AuditProgress(done, total, category.Name));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            Task.WaitAll(tasks);
            return results.ToList();
        }

        public static AuditCategory GetUsbPeripheralsCategory()
        {
            return RunCollector(UsbCategoryName, GetUsbPeripherals);
        }

        private static AuditCategory RunCollector(string name, Func<AuditCategory> collect)
        {
            AuditCategory category;
            try
            {
                category = collect();
            }
            catch
            {
                // A failing collector should never take the rest of the audit down with it.
                category = new AuditCategory { Name = name };
            }

            if (category.Items.Count == 0)
            {
                category.Items.Add(new AuditItem { Label = "Status", Value = $"No {category.Name.ToLower()} found", TooltipText = "No data available or blocked by spoofer", IsPlaceholder = true });
            }

            return category;
        }

        private static bool CheckIsAdministrator()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Yields each result and disposes it (plus the searcher and collection) once the caller moves on.
        private static IEnumerable<ManagementObject> QueryWmi(string query, string? scope = null)
        {
            using var searcher = scope == null
                ? new ManagementObjectSearcher(query)
                : new ManagementObjectSearcher(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    yield return obj;
                }
            }
        }

        private static string Text(object? value, string fallback = "")
        {
            return value?.ToString() ?? fallback;
        }

        // Tools are started by full path. A bare name would also search the app's own folder,
        // so a look-alike exe next to the download would run with this app's admin rights.
        private static readonly string ArpPath = Path.Combine(Environment.SystemDirectory, "ARP.EXE");
        private static readonly string[] NvidiaSmiPaths =
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        private static string? RunProcessOutput(string fileName, string arguments)
        {
            if (!File.Exists(fileName))
                return null;

            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using Process? process = Process.Start(psi);
                if (process == null)
                    return null;

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(ProcessTimeoutMilliseconds) || !outputTask.Wait(ProcessTimeoutMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                return outputTask.Result;
            }
            catch
            {
                return null;
            }
        }

        private static AuditCategory GetOperatingSystem()
        {
            var cat = new AuditCategory { Name = "Operating System" };
            try
            {
                foreach (ManagementObject obj in QueryWmi("SELECT Caption, OSArchitecture, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
                {
                    cat.Items.Add(new AuditItem { Label = "OS Name", Value = Text(obj["Caption"]), TooltipText = "Operating System Version" });
                    cat.Items.Add(new AuditItem { Label = "Architecture", Value = Text(obj["OSArchitecture"]), TooltipText = "OS Bit Architecture" });
                    cat.Items.Add(new AuditItem { Label = "Build Number", Value = Text(obj["BuildNumber"]), TooltipText = "Windows Build Number" });
                    cat.Items.Add(new AuditItem { Label = "Install Date", Value = FormatWmiDate(obj["InstallDate"]), TooltipText = "When Windows was installed" });
                    cat.Items.Add(new AuditItem { Label = "Last Boot", Value = FormatWmiDate(obj["LastBootUpTime"]), TooltipText = "System Uptime Reference" });
                }
            }
            catch { }

            // Registry IDs are read separately so a WMI failure cannot hide them.
            string machineGuid = GetRegistryValue(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid", "Unknown");
            string productId = GetRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId", "Unknown");

            cat.Items.Add(new AuditItem { Label = "Machine GUID", Value = machineGuid, TooltipText = "Cryptography Machine GUID" });
            cat.Items.Add(new AuditItem { Label = "Product ID", Value = productId, TooltipText = "Windows Product ID" });
            return cat;
        }

        private static string FormatWmiDate(object? value)
        {
            string? text = value?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return "Unknown";

            try
            {
                return ManagementDateTimeConverter.ToDateTime(text).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string GetRegistryValue(string keyPath, string valueName, string defaultValue = "Default string")
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (key != null)
                    {
                        var val = key.GetValue(valueName);
                        if (val != null) return val.ToString() ?? defaultValue;
                    }
                }
            }
            catch { }
            return defaultValue;
        }

        private static AuditCategory GetIdentityAndCryptography()
        {
            var cat = new AuditCategory { Name = "System Information" };
            try
            {
                string family = "Default string";
                foreach (ManagementObject obj in QueryWmi("SELECT Manufacturer, SystemFamily FROM Win32_ComputerSystem"))
                {
                    cat.Items.Add(new AuditItem { Label = "Manufacturer", Value = Text(obj["Manufacturer"]), TooltipText = "System Manufacturer" });
                    family = Text(obj["SystemFamily"], family);
                }

                foreach (ManagementObject obj in QueryWmi("SELECT Name, Version, IdentifyingNumber, UUID, SKUNumber FROM Win32_ComputerSystemProduct"))
                {
                    cat.Items.Add(new AuditItem { Label = "Product Name", Value = Text(obj["Name"]), TooltipText = "System Product Name" });
                    cat.Items.Add(new AuditItem { Label = "Version Index", Value = Text(obj["Version"]), TooltipText = "System Version" });
                    cat.Items.Add(new AuditItem { Label = "System Serial", Value = Text(obj["IdentifyingNumber"]), TooltipText = "System Serial Number" });
                    cat.Items.Add(new AuditItem { Label = "System UUID", Value = Text(obj["UUID"]), TooltipText = "SMBIOS UUID" });
                    cat.Items.Add(new AuditItem { Label = "Family Serial", Value = family, TooltipText = "SMBIOS system family" });
                    cat.Items.Add(new AuditItem { Label = "SKU Number", Value = Text(obj["SKUNumber"], "Default string"), TooltipText = "System SKU Number" });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetFirmwareAndSecurity()
        {
            var cat = new AuditCategory { Name = "BIOS Information" };
            try
            {
                foreach (ManagementObject obj in QueryWmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS"))
                {
                    cat.Items.Add(new AuditItem { Label = "BIOS Vendor", Value = Text(obj["Manufacturer"]), TooltipText = "BIOS Manufacturer" });
                    cat.Items.Add(new AuditItem { Label = "BIOS Version", Value = Text(obj["SMBIOSBIOSVersion"]), TooltipText = "BIOS Version String" });
                    string releaseDate = Text(obj["ReleaseDate"], "Unknown");
                    if (releaseDate.Length >= 8) releaseDate = $"{releaseDate.Substring(6, 2)}/{releaseDate.Substring(4, 2)}/{releaseDate.Substring(0, 4)}";
                    cat.Items.Add(new AuditItem { Label = "Release Date", Value = releaseDate, TooltipText = "BIOS Release Date" });
                    cat.Items.Add(new AuditItem { Label = "BIOS Serial", Value = Text(obj["SerialNumber"]), TooltipText = "BIOS Serial Number" });
                }

                // Core Isolation (HVCI)
                string hvciState = GetRegistryValue(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", "0");
                cat.Items.Add(new AuditItem { Label = "Core Isolation", Value = hvciState == "1" ? "Enabled" : "Disabled", TooltipText = "Hypervisor-Protected Code Integrity (HVCI)" });

                cat.Items.Add(new AuditItem { Label = "Virtualization", Value = GetVirtualizationState(), TooltipText = "Hardware virtualization (VT-x / AMD-V), or a running hypervisor" });

                // Secure Boot
                string secureBoot = GetRegistryValue(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", "0");
                cat.Items.Add(new AuditItem { Label = "Secure Boot", Value = secureBoot == "1" ? "Enabled" : "Disabled", TooltipText = "UEFI Secure Boot Status" });

            }
            catch { }
            return cat;
        }

        private static string GetVirtualizationState()
        {
            try
            {
                // With a hypervisor running (Hyper-V, VBS) the CPU reports its firmware flag as off,
                // so check for the hypervisor first.
                foreach (ManagementObject system in QueryWmi("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
                {
                    if (system["HypervisorPresent"] is true)
                        return "Enabled";
                }

                foreach (ManagementObject cpu in QueryWmi("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor"))
                {
                    if (cpu["VirtualizationFirmwareEnabled"] is bool enabled)
                        return enabled ? "Enabled" : "Disabled";
                }
            }
            catch { }

            return "Unknown";
        }

        private static AuditCategory GetTpmInformation()
        {
            var cat = new AuditCategory { Name = "TPM Information" };

            try
            {
                if (!IsAdministrator)
                {
                    cat.Items.Add(new AuditItem { Label = "TPM Status", Value = "Requires Admin", TooltipText = "Launch application as Administrator to query TPM" });
                    return cat;
                }

                bool foundTpm = false;
                foreach (ManagementObject tpm in QueryWmi(
                    "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, IsOwned_InitialValue, ManufacturerId, ManufacturerVersion, SpecVersion FROM Win32_Tpm",
                    @"root\CIMV2\Security\MicrosoftTpm"))
                {
                    foundTpm = true;
                    bool isEnabled = (bool)(tpm["IsEnabled_InitialValue"] ?? false);
                    bool isActivated = (bool)(tpm["IsActivated_InitialValue"] ?? false);
                    bool isOwned = (bool)(tpm["IsOwned_InitialValue"] ?? false);

                    string tpmStatus = "Present (Inactive)";
                    if (isEnabled && isActivated)
                    {
                        tpmStatus = isOwned ? "Ready & Owned" : "Present (Not Ready / Unowned)";
                    }

                    cat.Items.Add(new AuditItem { Label = "TPM Status", Value = tpmStatus, TooltipText = "Trusted Platform Module state" });

                    string mfgId = Text(tpm["ManufacturerId"], "Unknown");
                    cat.Items.Add(new AuditItem { Label = "Manufacturer ID", Value = mfgId, TooltipText = "TPM manufacturer identity" });
                    cat.Items.Add(new AuditItem { Label = "Manufacturer Version", Value = Text(tpm["ManufacturerVersion"], "Unknown"), TooltipText = "TPM firmware version" });
                    cat.Items.Add(new AuditItem { Label = "Spec Version", Value = Text(tpm["SpecVersion"], "Unknown"), TooltipText = "TPM specification version" });

                    TpmEndorsementInfo endorsement = GetTpmEndorsementInfo();
                    string endorsementTooltip = endorsement.Error.Length == 0
                        ? "Manufacturer endorsement-key certificate identity"
                        : $"Manufacturer endorsement-key certificate identity. Query detail: {endorsement.Error}";

                    cat.Items.Add(new AuditItem
                    {
                        Label = "Endorsement Key Serial Number",
                        Value = string.IsNullOrWhiteSpace(endorsement.SerialNumber) ? "Unavailable" : endorsement.SerialNumber,
                        TooltipText = endorsementTooltip
                    });
                    cat.Items.Add(new AuditItem
                    {
                        Label = "Endorsement Key Thumbprint",
                        Value = string.IsNullOrWhiteSpace(endorsement.Thumbprint) ? "Unavailable" : endorsement.Thumbprint,
                        TooltipText = endorsementTooltip
                    });
                }

                if (!foundTpm)
                {
                    cat.Items.Add(new AuditItem { Label = "TPM Status", Value = "Not Present / Disabled", TooltipText = "No TPM found in WMI" });
                }
            }
            catch (Exception ex)
            {
                cat.Items.Add(new AuditItem { Label = "TPM Status", Value = "Error accessing WMI", TooltipText = ex.Message });
            }

            return cat;
        }

        private static TpmEndorsementInfo GetTpmEndorsementInfo()
        {
            const string command =
                "$ErrorActionPreference='Stop';" +
                "Import-Module TrustedPlatformModule -ErrorAction Stop;" +
                "$ek=Get-TpmEndorsementKeyInfo -HashAlgorithm Sha256 -ErrorAction Stop;" +
                "$cert=@($ek.ManufacturerCertificates | Select-Object -First 1);" +
                "if($cert.Count -eq 0){$cert=@($ek.AdditionalCertificates | Select-Object -First 1)};" +
                "$serial='';$thumbprint='';" +
                "if($cert.Count -gt 0){$serial=[string]$cert[0].SerialNumber;$thumbprint=[string]$cert[0].Thumbprint};" +
                "[pscustomobject]@{SerialNumber=$serial;Thumbprint=$thumbprint;IsPresent=[bool]$ek.IsPresent}|ConvertTo-Json -Compress";

            try
            {
                string powershellPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = powershellPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);

                using Process process = new Process { StartInfo = psi };
                process.Start();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(15000))
                {
                    process.Kill(entireProcessTree: true);
                    return new TpmEndorsementInfo { Error = "PowerShell query timed out" };
                }

                string output = outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    string detail = string.IsNullOrWhiteSpace(error)
                        ? $"PowerShell exited with code {process.ExitCode}"
                        : error.Trim();
                    return new TpmEndorsementInfo { Error = ShortenError(detail) };
                }

                using JsonDocument json = JsonDocument.Parse(output.Trim());
                JsonElement root = json.RootElement;
                return new TpmEndorsementInfo
                {
                    SerialNumber = GetJsonString(root, "SerialNumber"),
                    Thumbprint = GetJsonString(root, "Thumbprint"),
                    Error = root.TryGetProperty("IsPresent", out JsonElement present) && !present.GetBoolean()
                        ? "The endorsement public key is not present"
                        : ""
                };
            }
            catch (Exception ex)
            {
                return new TpmEndorsementInfo { Error = ShortenError(ex.Message) };
            }
        }

        private static string GetJsonString(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim() ?? ""
                : "";
        }

        private static string ShortenError(string error)
        {
            string oneLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= 220 ? oneLine : oneLine[..220] + "…";
        }

        private sealed class TpmEndorsementInfo
        {
            public string SerialNumber { get; init; } = "";
            public string Thumbprint { get; init; } = "";
            public string Error { get; init; } = "";
        }

        private static AuditCategory GetMotherboard()
        {
            var cat = new AuditCategory { Name = "Baseboard Information" };
            try
            {
                // Win32_BaseBoard has no LocationInChassis property. The old reader threw on it after these
                // five rows, so "(CS) Location" never appeared; leaving it out keeps baselines identical.
                foreach (ManagementObject obj in QueryWmi("SELECT Manufacturer, Version, Product, SerialNumber, OtherIdentifyingInfo FROM Win32_BaseBoard"))
                {
                    cat.Items.Add(new AuditItem { Label = "Manufacturer", Value = Text(obj["Manufacturer"]), TooltipText = "Motherboard Manufacturer" });
                    cat.Items.Add(new AuditItem { Label = "Version Index", Value = Text(obj["Version"]), TooltipText = "Motherboard Revision" });
                    cat.Items.Add(new AuditItem { Label = "Product Name", Value = Text(obj["Product"]), TooltipText = "Motherboard Model" });
                    cat.Items.Add(new AuditItem { Label = "Serial Number", Value = Text(obj["SerialNumber"]), TooltipText = "Motherboard Serial Number" });
                    cat.Items.Add(new AuditItem { Label = "Asset Number", Value = Text(obj["OtherIdentifyingInfo"], "Default string"), TooltipText = "Asset Tag" });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetStorage()
        {
            var cat = NativeDiskQuery.GetDisks();

            // If native query failed to find any drives (e.g. strict anti-cheat/spoofer blocks IOCTLs completely)
            // we can fallback to WMI just as a last resort.
            if (cat.Items.Count == 0)
            {
                try
                {
                    foreach (ManagementObject obj in QueryWmi("SELECT Model, SerialNumber, UniqueId FROM MSFT_PhysicalDisk", @"Root\Microsoft\Windows\Storage"))
                    {
                        string model = obj["Model"]?.ToString()?.Trim() ?? "Unknown";
                        string serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "Unknown";
                        string uniqueId = obj["UniqueId"]?.ToString()?.Trim() ?? "";

                        if (string.IsNullOrEmpty(serial) || serial == "Unknown") continue;

                        string fallbackWwn = string.IsNullOrWhiteSpace(uniqueId) ? "00000000" : uniqueId;

                        cat.Items.Add(new AuditItem { Label = "DISK_STORAGE_MODEL", Value = model, TooltipText = "Drive Model" });
                        cat.Items.Add(new AuditItem { Label = "STORAGE_QUERY_PROPERTY", Value = serial, TooltipText = "MSFT_PhysicalDisk serial fallback (native IOCTL unavailable)" });
                        cat.Items.Add(new AuditItem { Label = "SMART_RCV_DRIVE_DATA", Value = "00000000", TooltipText = "Native SMART query unavailable" });
                        cat.Items.Add(new AuditItem { Label = "STORAGE_QUERY_WWN", Value = fallbackWwn, TooltipText = "MSFT_PhysicalDisk UniqueId fallback (native VPD query unavailable)" });
                        cat.Items.Add(new AuditItem { Label = "SCSI_PASS_THROUGH", Value = "00000000", TooltipText = "Native SCSI pass-through unavailable" });
                        cat.Items.Add(new AuditItem { Label = "ATA_PASS_THROUGH", Value = "00000000", TooltipText = "Native ATA pass-through unavailable" });
                        cat.Items.Add(new AuditItem { IsSeparator = true });
                    }
                }
                catch { }

                if (cat.Items.Count == 0)
                {
                    try
                    {
                        foreach (ManagementObject obj in QueryWmi("SELECT Model, SerialNumber FROM Win32_DiskDrive"))
                        {
                            string model = Text(obj["Model"], "Unknown");
                            string serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "Unknown";
                            if (string.IsNullOrEmpty(serial) || serial == "Unknown") continue;

                            cat.Items.Add(new AuditItem { Label = "DISK_STORAGE_MODEL", Value = model, TooltipText = "Drive Model" });
                            cat.Items.Add(new AuditItem { Label = "STORAGE_QUERY_PROPERTY", Value = serial, TooltipText = "Win32_DiskDrive serial fallback (native IOCTL unavailable)" });
                            cat.Items.Add(new AuditItem { Label = "SMART_RCV_DRIVE_DATA", Value = "00000000", TooltipText = "Native SMART query unavailable" });
                            cat.Items.Add(new AuditItem { Label = "STORAGE_QUERY_WWN", Value = "00000000", TooltipText = "Native VPD identifier query unavailable" });
                            cat.Items.Add(new AuditItem { Label = "SCSI_PASS_THROUGH", Value = "00000000", TooltipText = "Native SCSI pass-through unavailable" });
                            cat.Items.Add(new AuditItem { Label = "ATA_PASS_THROUGH", Value = "00000000", TooltipText = "Native ATA pass-through unavailable" });
                            cat.Items.Add(new AuditItem { IsSeparator = true });
                        }
                    }
                    catch { }
                }
            }

            return cat;
        }

        private static AuditCategory GetProcessor()
        {
            var cat = new AuditCategory { Name = "Processor Information" };
            try
            {
                foreach (ManagementObject obj in QueryWmi("SELECT Manufacturer, Name, SerialNumber, PartNumber, AssetTag, SocketDesignation FROM Win32_Processor"))
                {
                    cat.Items.Add(new AuditItem { Label = "CPU Manufacturer", Value = Text(obj["Manufacturer"]), TooltipText = "CPU Manufacturer" });
                    cat.Items.Add(new AuditItem { Label = "Processor Type", Value = Text(obj["Name"]), TooltipText = "CPU Model Name" });
                    cat.Items.Add(new AuditItem { Label = "Serial Number", Value = Text(obj["SerialNumber"], "Unknown"), TooltipText = "Processor Serial Number" });
                    cat.Items.Add(new AuditItem { Label = "Part Number", Value = Text(obj["PartNumber"], "Unknown"), TooltipText = "Processor Part Number" });
                    cat.Items.Add(new AuditItem { Label = "Asset Number", Value = Text(obj["AssetTag"], "Unknown"), TooltipText = "Processor Asset Tag" });
                    cat.Items.Add(new AuditItem { Label = "Processor Socket", Value = Text(obj["SocketDesignation"]), TooltipText = "Socket Type" });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetNetworkInterfaces()
        {
            var cat = new AuditCategory { Name = "Network Information" };
            try
            {
                // MAC Cache
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet || nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                // A virtual switch reports its NIC's MAC too; list each address once so repeats don't use up the four slots.
                var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nic in nics)
                {
                    string mac = nic.GetPhysicalAddress().ToString();
                    mac = string.Join(":", Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2)));
                    if (string.IsNullOrEmpty(mac) || !seenMacs.Add(mac))
                        continue;

                    cat.Items.Add(new AuditItem { Label = "MAC [Cache]", Value = mac, TooltipText = $"Active Spoofable MAC of {nic.Name}" });
                    if (seenMacs.Count == 4)
                        break;
                }

                // DHCP & DNS (Active Connection)
                try
                {
                    foreach (ManagementObject obj in QueryWmi("SELECT DHCPEnabled, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))
                    {
                        bool dhcp = (bool)(obj["DHCPEnabled"] ?? false);
                        string dnsStr = obj["DNSServerSearchOrder"] is string[] dns ? string.Join(", ", dns) : "None";
                        cat.Items.Add(new AuditItem { Label = "DHCP Enabled", Value = dhcp ? "Yes" : "No", TooltipText = "Is DHCP active?" });
                        cat.Items.Add(new AuditItem { Label = "DNS Servers", Value = dnsStr, TooltipText = "Active DNS Servers" });
                        break; // Just get the primary one
                    }
                }
                catch { }

                // MAC Kernel (Permanent)
                try
                {
                    foreach (ManagementObject obj in QueryWmi("SELECT PermanentAddress FROM MSFT_NetAdapter WHERE HardwareInterface=True", @"root\StandardCimv2"))
                    {
                        string? permMac = obj["PermanentAddress"]?.ToString();
                        if (!string.IsNullOrEmpty(permMac))
                        {
                            permMac = permMac.Replace("-", ":");
                            cat.Items.Add(new AuditItem { Label = "MAC [Kernel]", Value = permMac, TooltipText = "Burned-in Hardware MAC Address" });
                        }
                    }
                }
                catch { } // Fallback if MSFT_NetAdapter isn't available
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetArpInformation()
        {
            var cat = new AuditCategory { Name = "ARP Information" };
            try
            {
                string? output = RunProcessOutput(ArpPath, "-a");
                if (output == null)
                    return cat;

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                bool isFirst = true;
                int count = 0;
                foreach (var line in lines)
                {
                    if (count >= 10) break; // limit to prevent huge UI

                    if (line.StartsWith("Interface:", StringComparison.Ordinal))
                    {
                        if (isFirst) {
                            cat.Items.Add(new AuditItem { Label = line.Trim(), Value = "", TooltipText = "ARP Table Interface" });
                            isFirst = false;
                        }
                    }
                    else if (line.Contains("dynamic") || line.Contains("static"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            string ip = parts[0];
                            string mac = parts[1];
                            string type = parts[2];
                            cat.Items.Add(new AuditItem { Label = $"Physical Address: {mac}", Value = type, TooltipText = $"IP: {ip}" });
                            count++;
                        }
                    }
                }
            }
            catch { }
            return cat;
        }

        private static string DecodeWmiString(ushort[]? array)
        {
            if (array == null || array.Length == 0) return "Unknown";
            byte[] bytes = new byte[array.Length];
            for (int i = 0; i < array.Length; i++) bytes[i] = (byte)array[i];
            return Encoding.ASCII.GetString(bytes).Trim('\0');
        }

        private static AuditCategory GetMonitors()
        {
            var cat = new AuditCategory { Name = "Monitor Information" };
            try
            {
                foreach (ManagementObject obj in QueryWmi("SELECT ManufacturerName, UserFriendlyName, SerialNumberID FROM WmiMonitorID", @"root\wmi"))
                {
                    string mf = DecodeWmiString(obj["ManufacturerName"] as ushort[]);
                    string mn = DecodeWmiString(obj["UserFriendlyName"] as ushort[]);
                    string sn = DecodeWmiString(obj["SerialNumberID"] as ushort[]);

                    cat.Items.Add(new AuditItem { Label = "Manufacturer", Value = mf, TooltipText = "Monitor Manufacturer" });
                    cat.Items.Add(new AuditItem { Label = "Model Name", Value = mn, TooltipText = "Monitor Model" });
                    cat.Items.Add(new AuditItem { Label = "Monitor Serial", Value = sn, TooltipText = "EDID Monitor Serial" });
                    cat.Items.Add(new AuditItem { Label = "ID Serial Number", Value = "Unknown", TooltipText = "Device Instance ID" });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetGraphics()
        {
            var cat = new AuditCategory { Name = "GPU Information" };
            try
            {
                // nvidia-smi reports for the whole machine, so it only needs to run once per scan.
                string? nvidiaUuid = null;
                bool nvidiaQueried = false;

                foreach (ManagementObject obj in QueryWmi("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                {
                    string gpuName = Text(obj["Name"], "Unknown GPU");
                    cat.Items.Add(new AuditItem { Label = "PCI Device", Value = Text(obj["PNPDeviceID"]), TooltipText = "Hardware ID of the GPU" });
                    cat.Items.Add(new AuditItem { Label = "GPU Name", Value = gpuName, TooltipText = "Name of the Graphics Card" });

                    string uuid = "GPU-Unknown";
                    if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!nvidiaQueried)
                        {
                            nvidiaQueried = true;
                            string? smi = NvidiaSmiPaths.FirstOrDefault(File.Exists);
                            string? output = smi == null ? null : RunProcessOutput(smi, "--query-gpu=uuid --format=csv,noheader")?.Trim();
                            if (!string.IsNullOrEmpty(output) && output.StartsWith("GPU-", StringComparison.Ordinal))
                            {
                                nvidiaUuid = output;
                            }
                        }

                        if (nvidiaUuid != null)
                            uuid = nvidiaUuid;
                    }

                    cat.Items.Add(new AuditItem { Label = "GUID Serial", Value = uuid, TooltipText = "GPU GUID" });
                    cat.Items.Add(new AuditItem { IsSeparator = true });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetMemory()
        {
            var cat = new AuditCategory { Name = "Physical Memory (RAM)" };
            try
            {
                foreach (ManagementObject obj in QueryWmi("SELECT Manufacturer, PartNumber, SerialNumber, Capacity FROM Win32_PhysicalMemory"))
                {
                    string manufacturer = Text(obj["Manufacturer"], "Unknown");
                    string partNum = obj["PartNumber"]?.ToString()?.Trim() ?? "Unknown";
                    string serialNum = obj["SerialNumber"]?.ToString()?.Trim() ?? "Unknown";
                    ulong capacityBytes = (ulong)(obj["Capacity"] ?? 0UL);
                    string capacityStr = capacityBytes > 0 ? (capacityBytes / (1024 * 1024 * 1024)) + " GB" : "Unknown";

                    cat.Items.Add(new AuditItem { Label = "Manufacturer", Value = manufacturer, TooltipText = "RAM Manufacturer" });
                    cat.Items.Add(new AuditItem { Label = "Part Number", Value = partNum, TooltipText = "RAM Module Part Number" });
                    cat.Items.Add(new AuditItem { Label = "Serial Number", Value = serialNum, TooltipText = "RAM Serial Number" });
                    cat.Items.Add(new AuditItem { Label = "Capacity", Value = capacityStr, TooltipText = "Module Size" });
                    cat.Items.Add(new AuditItem { IsSeparator = true });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetVolumes()
        {
            var cat = new AuditCategory { Name = "Volume Serial Numbers" };
            try
            {
                foreach (ManagementObject obj in QueryWmi("SELECT DeviceID, VolumeSerialNumber FROM Win32_LogicalDisk WHERE DriveType = 3"))
                {
                    string deviceId = Text(obj["DeviceID"], "Unknown");
                    string volumeSerial = Text(obj["VolumeSerialNumber"], "Unknown");

                    if (volumeSerial != "Unknown" && volumeSerial.Length == 8)
                    {
                        volumeSerial = volumeSerial.Insert(4, "-");
                    }

                    cat.Items.Add(new AuditItem { Label = $"Volume [{deviceId}]", Value = volumeSerial, TooltipText = "OS Volume Serial Number" });
                }
            }
            catch { }
            return cat;
        }

        private static AuditCategory GetUsbPeripherals()
        {
            var cat = new AuditCategory { Name = UsbCategoryName };
            try
            {
                // Device instance IDs are case-insensitive. A case mismatch would list a connected
                // device as a ghost, and the cleanup would then remove it.
                var presentSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Get PRESENT devices via WMI (queried once, then resolved in memory)
                var devices = new List<(string PnpId, string? Name)>();
                foreach (ManagementObject obj in QueryWmi("SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'"))
                {
                    devices.Add((Text(obj["PNPDeviceID"]), obj["Name"]?.ToString()));
                }

                var vidPidToName = new Dictionary<string, string>();

                foreach (var (pnpId, rawName) in devices)
                {
                    string name = rawName ?? "";

                    // Explicitly skip generic names instead of anything containing "Controller"
                    if (name == "USB Composite Device" || name == "USB Input Device" || name.Contains("Hub") || name == "WinUsb Device")
                        continue;

                    var match = UsbVidPidRegex.Match(pnpId);
                    if (match.Success && !string.IsNullOrEmpty(name))
                    {
                        vidPidToName[match.Value] = name;
                    }
                }

                foreach (var (pnpId, rawName) in devices)
                {
                    string name = rawName ?? "Unknown USB Device";

                    var parts = pnpId.Split('\\');
                    if (parts.Length == 3)
                    {
                        string possibleSerial = parts[2];

                        bool isTrueSerial = !possibleSerial.Contains('&');
                        bool isXboxGeneratedSerial = possibleSerial.StartsWith("00&00&", StringComparison.Ordinal);

                        if (!string.IsNullOrEmpty(possibleSerial) && (isTrueSerial || isXboxGeneratedSerial))
                        {
                            string decodedSerial = DecodeHexStringIfValid(possibleSerial);
                            var match = UsbVidPidRegex.Match(pnpId);
                            if (match.Success && vidPidToName.TryGetValue(match.Value, out string? knownName))
                            {
                                name = knownName;
                            }

                            if (presentSerials.Add(possibleSerial))
                            {
                                cat.Items.Add(new AuditItem { Label = name, Value = decodedSerial, TooltipText = pnpId });
                            }
                        }
                    }
                }

                // 2. Get GHOST (Disconnected) devices from Registry
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (key != null)
                    {
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using (var subKey = key.OpenSubKey(subKeyName))
                            {
                                if (subKey != null)
                                {
                                    foreach (var instanceName in subKey.GetSubKeyNames())
                                    {
                                        bool isTrueGhostSerial = !instanceName.Contains('&');
                                        bool isXboxGhostGenerated = instanceName.StartsWith("00&00&", StringComparison.Ordinal);

                                        if ((isTrueGhostSerial || isXboxGhostGenerated) && !presentSerials.Contains(instanceName))
                                        {
                                            string friendlyName = "Ghost USB Device";
                                            using (var instKey = subKey.OpenSubKey(instanceName))
                                            {
                                                if (instKey != null)
                                                {
                                                    friendlyName = instKey.GetValue("FriendlyName")?.ToString()
                                                                ?? instKey.GetValue("DeviceDesc")?.ToString()
                                                                ?? "Ghost USB Device";

                                                    if (friendlyName.Contains(';'))
                                                        friendlyName = friendlyName.Split(';').Last();
                                                }
                                            }

                                            string decodedGhostSerial = DecodeHexStringIfValid(instanceName);
                                            string instanceId = $@"USB\{subKeyName}\{instanceName}";
                                            cat.Items.Add(new AuditItem { Label = friendlyName + " [GHOST]", Value = decodedGhostSerial, TooltipText = instanceId });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return cat;
        }

        private static string DecodeHexStringIfValid(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return hex;

            var chars = new char[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (!byte.TryParse(hex.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b) || b < 32 || b > 126)
                    return hex;

                chars[i / 2] = (char)b;
            }

            return new string(chars);
        }
    }
}
