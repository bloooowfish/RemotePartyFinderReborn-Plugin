using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace RemotePartyFinderReborn;

internal sealed class SettingsTabRenderer
{
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private string _uploadUrlTempString = string.Empty;
    private readonly ExpiringUiStatus _uploadUrlError = new();
    private string _playerManualUploadStatus = string.Empty;
    private string _fflogsCooldownStatus = string.Empty;
    private string _tomestoneCooldownStatus = string.Empty;

    public SettingsTabRenderer(Plugin plugin)
    {
        _plugin = plugin;
        _configuration = plugin.Configuration;
    }

    public void Draw()
    {
        DrawFFLogsApiSettingsSection();
        DrawSeparatorSpacing();
        DrawTomestoneApiSettingsSection();
        DrawSeparatorSpacing();
        DrawEndpointSettingsSection();
        DrawSeparatorSpacing();
        DrawUploadBehaviorSection();
        DrawSeparatorSpacing();
        DrawFFLogsWorkerSection();
        DrawSeparatorSpacing();
        DrawMaintenanceSection();
    }

    private void DrawFFLogsApiSettingsSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[FFLogs API Settings]");
        ImGui.Spacing();

        var clientId = _configuration.FFLogsClientId;
        if (ImGui.InputText("Client ID", ref clientId, 100))
        {
            _configuration.FFLogsClientId = clientId;
            _configuration.Save();
        }

        var clientSecret = _configuration.FFLogsClientSecret;
        if (ImGui.InputText("Client Secret", ref clientSecret, 100, ImGuiInputTextFlags.Password))
        {
            _configuration.FFLogsClientSecret = clientSecret;
            _configuration.Save();
        }

        var enableWorker = _configuration.EnableFFLogsWorker;
        if (ImGui.Checkbox("Enable FFLogs background worker", ref enableWorker))
        {
            _configuration.EnableFFLogsWorker = enableWorker;
            _configuration.Save();
        }

        if (ImGui.CollapsingHeader("How to get a client ID and a client secret:"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Bullet();
            ImGui.Text("Open https://www.fflogs.com/api/clients/ or");
            ImGui.SameLine();
            if (ImGui.Button("Click here##APIClientLink"))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.fflogs.com/api/clients/",
                    UseShellExecute = true
                });
            }

            ImGui.AlignTextToFramePadding();
            ImGui.Bullet();
            ImGui.Text("Create a new client");

            ImGui.AlignTextToFramePadding();
            ImGui.Bullet();
            ImGui.Text("Choose any name, for example: \"Plugin\"");
            ImGui.SameLine();
            if (ImGui.Button("Copy##APIClientCopyName"))
            {
                CopyToClipboard("Plugin");
            }

            ImGui.AlignTextToFramePadding();
            ImGui.Bullet();
            ImGui.Text("Enter any URL, for example: \"https://www.example.com\"");
            ImGui.SameLine();
            if (ImGui.Button("Copy##APIClientCopyURL"))
            {
                CopyToClipboard("https://www.example.com");
            }

            ImGui.AlignTextToFramePadding();
            ImGui.Bullet();
            ImGui.Text("Do NOT check the Public Client option");

            ImGui.AlignTextToFramePadding();
            ImGui.Bullet();
            ImGui.Text("Paste both client ID and secret above");
        }
    }

    private void DrawTomestoneApiSettingsSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[Tomestone API Settings]");
        ImGui.Spacing();

        var apiKey = _configuration.TomestoneApiKey;
        if (ImGui.InputText("API Key##TomestoneApiKey", ref apiKey, 200, ImGuiInputTextFlags.Password))
        {
            _configuration.TomestoneApiKey = apiKey;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Stored locally and sent only as an Authorization bearer token for Tomestone API requests.");
        }

        var enableProgressEnrichment = _configuration.EnableTomestoneProgressEnrichment;
        if (ImGui.Checkbox("Enable Tomestone progress enrichment", ref enableProgressEnrichment))
        {
            _configuration.EnableTomestoneProgressEnrichment = enableProgressEnrichment;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Enables official Tomestone API progression lookups. Tomestone failures do not block FFLogs uploads.");
        }

        var requestIntervalMs = Configuration.NormalizeTomestoneRequestIntervalMs(_configuration.TomestoneRequestIntervalMs);
        if (ImGui.SliderInt("Tomestone Request Interval", ref requestIntervalMs, 0, 60000, "%d ms"))
        {
            _configuration.TomestoneRequestIntervalMs = Configuration.NormalizeTomestoneRequestIntervalMs(requestIntervalMs);
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Delay between Tomestone request waves. With max concurrency 1, this is the delay between requests.");
        }

        requestIntervalMs = Configuration.NormalizeTomestoneRequestIntervalMs(_configuration.TomestoneRequestIntervalMs);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Tomestone Request Interval (ms)", ref requestIntervalMs, 50, 250))
        {
            _configuration.TomestoneRequestIntervalMs = Configuration.NormalizeTomestoneRequestIntervalMs(requestIntervalMs);
            _configuration.Save();
        }

        var maxConcurrency = Configuration.NormalizeTomestoneMaxConcurrency(_configuration.TomestoneMaxConcurrency);
        if (ImGui.SliderInt("Tomestone Max Concurrency", ref maxConcurrency, 1, 3, "%d"))
        {
            _configuration.TomestoneMaxConcurrency = Configuration.NormalizeTomestoneMaxConcurrency(maxConcurrency);
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Maximum number of Tomestone progress requests started in the same wave.");
        }
    }

    private void DrawEndpointSettingsSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[Endpoint Settings]");
        ImGui.Spacing();
        ImGui.TextWrapped("Configure which endpoints receive party finder data.");
        ImGui.Spacing();

        using (ImRaii.Table((ImU8String)"uploadUrls", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("URL", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            using var id = ImRaii.PushId((ImU8String)"urls");
            foreach (var (uploadUrl, index) in _configuration.UploadUrls.Select((url, index) => (url, index + 1)))
            {
                id.Push(index);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(index.ToString());

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(uploadUrl.Url);

                ImGui.TableSetColumnIndex(2);
                var isEnabled = uploadUrl.IsEnabled;
                if (ImGui.Checkbox("##uploadUrlCheckbox", ref isEnabled))
                {
                    uploadUrl.IsEnabled = isEnabled;
                    _configuration.Save();
                }

                if (!uploadUrl.IsDefault)
                {
                    ImGui.SameLine();
                    if (ImGuiComponents.IconButton(Dalamud.Interface.FontAwesomeIcon.Trash))
                    {
                        _configuration.UploadUrls = _configuration.UploadUrls.Remove(uploadUrl);
                        _configuration.Save();
                    }
                }

                id.Pop();
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##uploadUrlInput", ref _uploadUrlTempString, 300);
            ImGui.TableNextColumn();

            if (!string.IsNullOrEmpty(_uploadUrlTempString) &&
                ImGuiComponents.IconButton(Dalamud.Interface.FontAwesomeIcon.Plus))
            {
                _uploadUrlTempString = _uploadUrlTempString.TrimEnd();

                if (_configuration.UploadUrls.Any(r =>
                        string.Equals(r.Url, _uploadUrlTempString, StringComparison.InvariantCultureIgnoreCase)))
                {
                    SetUploadUrlError("Endpoint already exists.");
                }
                else if (!IngestEndpointResolver.IsValidUploadUrl(_uploadUrlTempString, out var validationError))
                {
                    SetUploadUrlError(validationError);
                }
                else
                {
                    _configuration.UploadUrls = _configuration.UploadUrls.Add(new(_uploadUrlTempString));
                    _configuration.Save();
                    _uploadUrlTempString = string.Empty;
                }
            }
        }

        ImGui.Dummy(new Vector2(0, 5));

        if (ImGui.Button("Reset To Default##uploadUrlDefault"))
        {
            ResetToDefault();
        }

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1, 0, 0, 1), _uploadUrlError.Current(DateTime.UtcNow));

        DrawSeparatorSpacing();

        ImGui.Text("Endpoint Status:");

        if (ImGui.BeginTable("cbStatusTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("URL", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Failures", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var url in _configuration.UploadUrls)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text(url.Url);

                ImGui.TableSetColumnIndex(1);
                var circuitStatus = UploadAttemptRecorder.GetCircuitStatus(url, _configuration, DateTime.UtcNow);
                ImGui.Text($"{circuitStatus.FailureCount} / {_configuration.CircuitBreakerFailureThreshold}");

                ImGui.TableSetColumnIndex(2);
                if (circuitStatus.FailureCount >= _configuration.CircuitBreakerFailureThreshold)
                {
                    if (circuitStatus.IsOpen)
                    {
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), $"OPEN ({circuitStatus.RemainingBreakDuration.TotalSeconds:F0}s)");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1, 1, 0, 1), "HALF-OPEN");
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), "CLOSED");
                }

                ImGui.TableSetColumnIndex(3);
                if (ImGui.Button($"Reset##{url.GetHashCode()}"))
                {
                    UploadAttemptRecorder.RecordSuccess(url);
                    _configuration.Save();
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawUploadBehaviorSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[Upload Behavior]");
        ImGui.Spacing();

        var threshold = _configuration.CircuitBreakerFailureThreshold;
        if (ImGui.SliderInt("Failure Threshold", ref threshold, 1, 20, "%d failures"))
        {
            _configuration.CircuitBreakerFailureThreshold = threshold;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Number of consecutive failures before pausing uploads.");
        }

        var duration = _configuration.CircuitBreakerBreakDurationMinutes;
        if (ImGui.SliderInt("Break Duration", ref duration, 1, 60, "%d min"))
        {
            _configuration.CircuitBreakerBreakDurationMinutes = duration;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Duration to pause uploads after threshold is reached.");
        }

        var detailUploadRetryCount = _configuration.DetailUploadRetryCount;
        if (ImGui.SliderInt("Detail Upload Retries", ref detailUploadRetryCount, 0, 5, "%d"))
        {
            _configuration.DetailUploadRetryCount = detailUploadRetryCount;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Additional retries after the initial PF detail upload attempt.");
        }

        var charaCardResolveRetryCount = _configuration.CharaCardResolveRetryCount;
        if (ImGui.SliderInt("Plate Resolve Retries", ref charaCardResolveRetryCount, 0, 5, "%d"))
        {
            _configuration.CharaCardResolveRetryCount = charaCardResolveRetryCount;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Additional retries after the initial contentId-to-plate resolve attempt.");
        }

        var autoDetailScanRetryCount = _configuration.AutoDetailScanRetryCount;
        if (ImGui.SliderInt("Auto Scanner Retries", ref autoDetailScanRetryCount, 0, 5, "%d"))
        {
            _configuration.AutoDetailScanRetryCount = autoDetailScanRetryCount;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Additional retries after the initial PF auto scanner interaction attempt.");
        }
    }

    private void DrawFFLogsWorkerSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[FFLogs Worker]");
        ImGui.Spacing();

        var workerBaseDelay = _configuration.FFLogsWorkerBaseDelayMs;
        if (ImGui.SliderInt("Base Delay", ref workerBaseDelay, 1000, 30000, "%d ms"))
        {
            _configuration.FFLogsWorkerBaseDelayMs = workerBaseDelay;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Base delay used for disabled/misconfigured states and exponential backoff base.");
        }

        var workerIdleDelay = _configuration.FFLogsWorkerIdleDelayMs;
        if (ImGui.SliderInt("Idle Delay", ref workerIdleDelay, 1000, 60000, "%d ms"))
        {
            _configuration.FFLogsWorkerIdleDelayMs = workerIdleDelay;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Polling delay when there is no work or after successful processing.");
        }

        var workerMaxBackoffDelay = _configuration.FFLogsWorkerMaxBackoffDelayMs;
        if (ImGui.SliderInt("Max Backoff", ref workerMaxBackoffDelay, 5000, 180000, "%d ms"))
        {
            _configuration.FFLogsWorkerMaxBackoffDelayMs = workerMaxBackoffDelay;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Upper bound for retry backoff after repeated transient failures.");
        }

        var workerJitter = _configuration.FFLogsWorkerJitterMs;
        if (ImGui.SliderInt("Delay Jitter", ref workerJitter, 0, 10000, "%d ms"))
        {
            _configuration.FFLogsWorkerJitterMs = workerJitter;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Random jitter added to worker delays to reduce synchronized polling spikes.");
        }
    }

    private void DrawMaintenanceSection()
    {
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[Maintenance]");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[FFLogs API Rate-Limit Lockout]");

        if (_plugin.TryGetFFLogsRateLimitCooldownRemaining(out var cooldownRemaining))
        {
            var untilLocal = FormatLocalClock(_plugin.FFLogsRateLimitCooldownUntilUtc);
            ImGui.TextColored(
                new Vector4(1.0f, 0.8f, 0.2f, 1.0f),
                $"ACTIVE - remaining {cooldownRemaining.TotalMinutes:F1} min (until {untilLocal} Local)");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "INACTIVE");
        }

        if (ImGui.Button("Reset FFLogs Rate-Limit Lockout"))
        {
            _plugin.ResetFFLogsRateLimitCooldown();
            _fflogsCooldownStatus = $"[{DateTime.Now:HH:mm:ss}] FFLogs rate-limit lockout reset";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Clears the local 1-hour FFLogs 429 lockout immediately.");
        }

        if (!string.IsNullOrEmpty(_fflogsCooldownStatus))
        {
            ImGui.TextWrapped(_fflogsCooldownStatus);
        }

        DrawSeparatorSpacing();

        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[Tomestone API Rate-Limit Lockout]");

        if (_plugin.TryGetTomestoneRateLimitCooldownRemaining(out var tomestoneCooldownRemaining))
        {
            var untilLocal = FormatLocalClock(_plugin.TomestoneRateLimitCooldownUntilUtc);
            ImGui.TextColored(
                new Vector4(1.0f, 0.8f, 0.2f, 1.0f),
                $"ACTIVE - remaining {tomestoneCooldownRemaining.TotalMinutes:F1} min (until {untilLocal} Local)");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "INACTIVE");
        }

        if (ImGui.Button("Reset Tomestone Rate-Limit Lockout"))
        {
            _plugin.ResetTomestoneRateLimitCooldown();
            _tomestoneCooldownStatus = $"[{DateTime.Now:HH:mm:ss}] Tomestone rate-limit lockout reset";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Clears the local 1-hour Tomestone API 429 lockout immediately.");
        }

        if (!string.IsNullOrEmpty(_tomestoneCooldownStatus))
        {
            ImGui.TextWrapped(_tomestoneCooldownStatus);
        }

        DrawSeparatorSpacing();

        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "[Player Cache Manual Sync]");
        ImGui.TextUnformatted($"Pending Player Cache Rows: {_plugin.PendingPlayerUploadCount}");
        ImGui.TextUnformatted($"Player Upload Worker: {(_plugin.IsPlayerUploadInProgress ? "BUSY" : "IDLE")}");

        if (ImGui.Button("Upload Pending Players Now"))
        {
            _plugin.TriggerPlayerUploadNow();
            _playerManualUploadStatus = $"Triggered pending upload at {DateTime.Now:HH:mm:ss} (Local)";
        }

        ImGui.SameLine();
        if (ImGui.Button("Requeue All Cached Players + Upload"))
        {
            var requeued = _plugin.TriggerPlayerFullResyncUploadNow();
            _playerManualUploadStatus = $"Requeued {requeued} cached rows and triggered upload at {DateTime.Now:HH:mm:ss} (Local)";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Marks every row in player_cache.db as dirty, then starts upload immediately.");
        }

        ImGui.TextWrapped("Chat commands: /rpr players-upload, /rpr players-resync");
        if (!string.IsNullOrEmpty(_playerManualUploadStatus))
        {
            ImGui.TextWrapped(_playerManualUploadStatus);
        }
    }

    private static void DrawSeparatorSpacing()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void ResetToDefault()
    {
        _configuration.UploadUrls = Configuration.DefaultUploadUrls();
        _configuration.Save();
    }

    private void SetUploadUrlError(string message)
    {
        _uploadUrlError.Set(message, DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    private static string FormatLocalClock(DateTime utcTime)
    {
        if (utcTime == DateTime.MinValue)
        {
            return "-";
        }

        var normalizedUtc = utcTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)
            : utcTime;
        return normalizedUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            ImGui.SetClipboardText(text);
        }
        catch (Exception)
        {
            // Ignore
        }
    }
}
