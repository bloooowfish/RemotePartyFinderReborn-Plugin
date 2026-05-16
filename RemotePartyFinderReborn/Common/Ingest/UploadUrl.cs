using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RemotePartyFinderReborn;

public record UploadUrl(string Url)
{
    public string Url { get; set; } = Url;
    public bool IsDefault { get; init; }
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    private readonly UploadCircuitState _circuitState = new();

    [JsonIgnore]
    public int FailureCount
    {
        get => _circuitState.FailureCount;
        set => _circuitState.FailureCount = value;
    }

    [JsonIgnore]
    public DateTime LastFailureTime
    {
        get => _circuitState.LastFailureTime;
        set => _circuitState.LastFailureTime = value;
    }

    [JsonIgnore]
    public DateTime LastSecurityWarningTime { get; set; }

    [JsonIgnore]
    public bool DetailBatchUnsupported { get; set; }

    [JsonIgnore]
    private readonly object _capabilityLock = new();

    [JsonIgnore]
    private readonly ProtectedCapabilityStore _protectedCapabilities = new();

    [JsonIgnore]
    private readonly DetailCapabilityStore _detailCapabilities = new();

    internal bool ShouldLogSecurityWarning(DateTime utcNow, TimeSpan cooldown)
    {
        lock (_capabilityLock)
        {
            if (utcNow - LastSecurityWarningTime < cooldown)
            {
                return false;
            }

            LastSecurityWarningTime = utcNow;
            return true;
        }
    }

    internal void ResetFailureState()
    {
        _circuitState.ResetFailureState();
    }

    internal void RecordFailure(DateTime utcNow)
    {
        _circuitState.RecordFailure(utcNow);
    }

    internal void MarkDetailBatchUnsupported()
    {
        lock (_capabilityLock)
        {
            DetailBatchUnsupported = true;
        }
    }

    internal bool IsCircuitOpen(int failureThreshold, int breakDurationMinutes, DateTime utcNow)
    {
        return _circuitState.IsCircuitOpen(failureThreshold, breakDurationMinutes, utcNow);
    }

    internal void ApplyIngestCapabilities(
        ProtectedEndpointCapabilities protectedEndpoints,
        IEnumerable<ListingDetailCapability> detailCapabilities,
        DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        ApplyProtectedCapabilities(protectedEndpoints, observedNow);
        ApplyDetailCapabilities(detailCapabilities, observedNow);
    }

    internal void ApplyProtectedCapabilities(
        ProtectedEndpointCapabilities protectedEndpoints,
        DateTimeOffset? now = null)
    {
        _protectedCapabilities.ApplyProtectedCapabilities(protectedEndpoints, now);
    }

    internal bool ShouldDeferProtectedRequest(
        ProtectedEndpointCapabilityKind kind,
        DateTimeOffset? now = null)
    {
        return _protectedCapabilities.ShouldDeferProtectedRequest(kind, now);
    }

    internal bool ShouldDeferDetailRequest(uint listingId, DateTimeOffset? now = null)
    {
        return _detailCapabilities.ShouldDeferDetailRequest(listingId, now);
    }

    internal void RequireProtectedCapabilities()
    {
        _protectedCapabilities.RequireProtectedCapabilities();
    }

    internal void MarkDetailCapabilitiesRequired()
    {
        _detailCapabilities.MarkDetailCapabilitiesRequired();
    }

    internal bool TryGetProtectedCapability(
        ProtectedEndpointCapabilityKind kind,
        out string token,
        DateTimeOffset? now = null)
    {
        return _protectedCapabilities.TryGetProtectedCapability(kind, out token, now);
    }

    internal void InvalidateProtectedCapability(ProtectedEndpointCapabilityKind kind)
    {
        _protectedCapabilities.InvalidateProtectedCapability(kind);
    }

    internal bool TryGetDetailCapability(uint listingId, out string token, DateTimeOffset? now = null)
    {
        return _detailCapabilities.TryGetDetailCapability(listingId, out token, now);
    }

    internal void InvalidateDetailCapability(uint listingId)
    {
        _detailCapabilities.InvalidateDetailCapability(listingId);
    }

    internal void ApplyDetailCapabilities(
        IEnumerable<ListingDetailCapability> detailCapabilities,
        DateTimeOffset? now = null)
    {
        _detailCapabilities.ApplyDetailCapabilities(detailCapabilities, now);
    }
}
