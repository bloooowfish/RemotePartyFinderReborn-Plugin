using System;

#nullable enable

namespace RemotePartyFinderReborn;

internal sealed class PartyDetailRequestCoordinator {
    private const uint PfDetailAddonId = 275U;

    private readonly object _gate = new();
    private readonly ScannerHeadlessBackendKind _scannerHeadlessBackendKind;
    private ScannerAttempt? _scannerAttempt;
    private ActiveScannerIntercept? _activeScannerIntercept;
    private PartyDetailScannerHeadlessScope? _activeScannerHeadlessScope;

    internal PartyDetailRequestCoordinator(
        ScannerHeadlessBackendKind scannerHeadlessBackendKind = ScannerHeadlessBackendKind.Disabled
    ) {
        _scannerHeadlessBackendKind = scannerHeadlessBackendKind;
    }

    internal long? CurrentScannerRequestSerial {
        get {
            lock (_gate) {
                return _scannerAttempt?.RequestSerial;
            }
        }
    }

    internal bool HasActiveScannerInteraction {
        get {
            lock (_gate) {
                return _scannerAttempt is not null
                       || _activeScannerIntercept is not null
                       || _activeScannerHeadlessScope is not null;
            }
        }
    }

    internal void ArmScannerRequest(Guid attemptId, ulong listingId, ulong contentId, bool allowHeadless) {
        lock (_gate) {
            if (_scannerAttempt is { AttemptId: var existingAttemptId } existing && existingAttemptId == attemptId) {
                _scannerAttempt = existing with {
                    ListingId = listingId,
                    ContentId = contentId,
                    AllowHeadless = allowHeadless,
                };
                return;
            }

            _scannerAttempt = new ScannerAttempt(attemptId, listingId, contentId, null, allowHeadless);
        }
    }

    internal void BeginScannerOpenAttempt(Guid attemptId) {
        lock (_gate) {
            if (_scannerAttempt is not { AttemptId: var armedAttemptId } attempt || armedAttemptId != attemptId) {
                return;
            }

            _activeScannerIntercept = new ActiveScannerIntercept(attempt.AttemptId, attempt.ListingId, attempt.ContentId);
        }
    }

    internal void EndScannerOpenAttempt(Guid attemptId) {
        lock (_gate) {
            if (_activeScannerIntercept is { AttemptId: var activeAttemptId } && activeAttemptId == attemptId) {
                _activeScannerIntercept = null;
            }
        }
    }

    internal void ClearScannerRequest(Guid attemptId) {
        lock (_gate) {
            if (_scannerAttempt is { AttemptId: var armedAttemptId } && armedAttemptId == attemptId) {
                _scannerAttempt = null;
            }

            if (_activeScannerIntercept is { AttemptId: var activeAttemptId } && activeAttemptId == attemptId) {
                _activeScannerIntercept = null;
            }

            if (_activeScannerHeadlessScope is { AttemptId: var activeHeadlessAttemptId } && activeHeadlessAttemptId == attemptId) {
                _activeScannerHeadlessScope = null;
            }
        }
    }

    internal void ResetScannerRequest() {
        lock (_gate) {
            _scannerAttempt = null;
            _activeScannerIntercept = null;
            _activeScannerHeadlessScope = null;
        }
    }

    internal void CompleteScannerRequest(Guid attemptId, PartyDetailScannerAttemptOutcome outcome) {
        if (ShouldClearScannerRequest(outcome)) {
            ClearScannerRequest(attemptId);
        }
    }

    internal long? GetArmedScannerRequestSerial(Guid attemptId) {
        lock (_gate) {
            return _scannerAttempt is { AttemptId: var armedAttemptId } attempt && armedAttemptId == attemptId
                ? attempt.RequestSerial
                : null;
        }
    }

    internal bool TryGetScannerAttemptDebugState(out PartyDetailScannerAttemptDebugState debugState) {
        lock (_gate) {
            if (_scannerAttempt is not { } attempt) {
                debugState = default;
                return false;
            }

            debugState = new PartyDetailScannerAttemptDebugState(
                attempt.RequestSerial,
                attempt.AllowHeadless,
                attempt.ListingId,
                attempt.ContentId
            );
            return true;
        }
    }

    internal PartyDetailRequestCycle EnsureRequestCycleForOpenIntercept(
        PartyDetailCaptureState state,
        ulong listingId,
        ulong contentId
    ) {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate) {
            if (_scannerAttempt is { } attempt
                && _activeScannerIntercept is { } activeIntercept
                && activeIntercept.AttemptId == attempt.AttemptId
                && IsCompatible(activeIntercept.ListingId, activeIntercept.ContentId, listingId, contentId)) {
                if (!attempt.RequestSerial.HasValue) {
                    var scannerCycle = state.BeginRequest(
                        PartyDetailRequestOwner.Scanner,
                        attempt.ListingId != 0 ? attempt.ListingId : listingId,
                        attempt.ContentId != 0 ? attempt.ContentId : contentId
                    );

                    _scannerAttempt = attempt with {
                        ListingId = scannerCycle.ListingId,
                        ContentId = scannerCycle.ContentId,
                        RequestSerial = scannerCycle.RequestSerial,
                    };
                    return scannerCycle;
                }

                if (state.TryGetCurrentRequestCycle(out var existingCycle)
                    && existingCycle.RequestSerial == attempt.RequestSerial.Value) {
                    return existingCycle;
                }
            }

            return state.BeginRequest(PartyDetailRequestOwner.Manual, listingId, contentId);
        }
    }

    internal bool ShouldSuppressScannerPfDetailOpen(PartyDetailCaptureState state, uint addonId) {
        ArgumentNullException.ThrowIfNull(state);

        if (_scannerHeadlessBackendKind != ScannerHeadlessBackendKind.NativeSuppression || addonId != PfDetailAddonId) {
            return false;
        }

        lock (_gate) {
            if (_scannerAttempt is not { RequestSerial: var requestSerial, AllowHeadless: true } attempt
                || !requestSerial.HasValue) {
                return false;
            }

            if (state.CurrentRequestSerial != requestSerial.Value) {
                return false;
            }

            if (!state.TryGetCurrentRequestCycle(out var cycle)) {
                return true;
            }

            return cycle.Owner == PartyDetailRequestOwner.Scanner
                   && cycle.RequestSerial == requestSerial.Value
                   && IsCompatible(attempt.ListingId, attempt.ContentId, cycle.ListingId, cycle.ContentId);
        }
    }

    internal bool ShouldSuppressScannerGameLog(PartyDetailCaptureState state, uint logMessageId) {
        ArgumentNullException.ThrowIfNull(state);

        var activeCandidate = new GameLogSuppressionContext(
            GameLogSuppressionFeature.PartyDetailScanner,
            IsSuppressionActive: true,
            IsScannerOwnedCapture: true
        );
        if (!GameLogSuppressionPolicy.ShouldSuppress(logMessageId, activeCandidate)) {
            return false;
        }

        lock (_gate) {
            if (_scannerAttempt is not { RequestSerial: var requestSerial } attempt
                || !requestSerial.HasValue) {
                return false;
            }

            if (state.CurrentRequestSerial != requestSerial.Value) {
                return false;
            }

            if (!state.TryGetCurrentRequestCycle(out var cycle)) {
                return false;
            }

            var isScannerOwnedCapture = cycle.Owner == PartyDetailRequestOwner.Scanner
                                        && cycle.RequestSerial == requestSerial.Value
                                        && IsCompatible(attempt.ListingId, attempt.ContentId, cycle.ListingId, cycle.ContentId);
            return GameLogSuppressionPolicy.ShouldSuppress(
                logMessageId,
                activeCandidate with {
                    IsScannerOwnedCapture = isScannerOwnedCapture,
                }
            );
        }
    }

    internal bool IsConsumedAckReady(
        PartyDetailCaptureState state,
        long requestSerial,
        ulong listingId,
        ulong contentId
    ) {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsScannerConsumedAckReady(requestSerial, listingId, contentId);
    }

    internal bool TryBeginScannerHeadlessScope(PartyDetailCaptureState state, Guid attemptId) {
        ArgumentNullException.ThrowIfNull(state);

        if (_scannerHeadlessBackendKind != ScannerHeadlessBackendKind.PopulateCorrelation) {
            return false;
        }

        lock (_gate) {
            if (_activeScannerHeadlessScope is { AttemptId: var activeAttemptId } && activeAttemptId == attemptId) {
                return true;
            }

            if (_scannerAttempt is not { AttemptId: var armedAttemptId, RequestSerial: var requestSerial, AllowHeadless: true } attempt
                || armedAttemptId != attemptId
                || !requestSerial.HasValue) {
                return false;
            }

            if (!state.TryGetCurrentRequestCycle(out var cycle)
                || cycle.RequestSerial != requestSerial.Value
                || cycle.Owner != PartyDetailRequestOwner.Scanner) {
                return false;
            }

            _activeScannerHeadlessScope = new PartyDetailScannerHeadlessScope(
                attemptId,
                cycle.RequestSerial,
                cycle.ListingId,
                cycle.ContentId
            );
            return true;
        }
    }

    internal void EndScannerHeadlessScope(Guid attemptId) {
        lock (_gate) {
            if (_activeScannerHeadlessScope is { AttemptId: var activeAttemptId } && activeAttemptId == attemptId) {
                _activeScannerHeadlessScope = null;
            }
        }
    }

    internal bool TryGetActiveScannerHeadlessScope(Guid attemptId, out PartyDetailScannerHeadlessScope scope) {
        lock (_gate) {
            if (_activeScannerHeadlessScope is not { AttemptId: var activeAttemptId } activeScope
                || activeAttemptId != attemptId) {
                scope = default;
                return false;
            }

            scope = activeScope;
            return true;
        }
    }

    internal static bool ShouldClearScannerRequest(PartyDetailScannerAttemptOutcome outcome) {
        return outcome switch {
            PartyDetailScannerAttemptOutcome.OpenFailed => true,
            PartyDetailScannerAttemptOutcome.TimedOut => true,
            PartyDetailScannerAttemptOutcome.Succeeded => true,
            _ => false,
        };
    }

    private static bool IsCompatible(ulong armedListingId, ulong armedContentId, ulong listingId, ulong contentId) {
        if (armedListingId != 0 && listingId != 0 && armedListingId != listingId) {
            return false;
        }

        if (armedContentId != 0 && contentId != 0 && armedContentId != contentId) {
            return false;
        }

        return true;
    }

    private sealed record ScannerAttempt(Guid AttemptId, ulong ListingId, ulong ContentId, long? RequestSerial, bool AllowHeadless);
    private sealed record ActiveScannerIntercept(Guid AttemptId, ulong ListingId, ulong ContentId);
}

internal readonly record struct PartyDetailScannerAttemptDebugState(
    long? RequestSerial,
    bool AllowHeadless,
    ulong ListingId,
    ulong ContentId
);

internal readonly record struct PartyDetailScannerHeadlessScope(
    Guid AttemptId,
    long RequestSerial,
    ulong ListingId,
    ulong ContentId
);
